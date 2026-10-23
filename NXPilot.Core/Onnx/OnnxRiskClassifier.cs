// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using NXPilot.Risk;

namespace NXPilot.Onnx;

/// <summary>Onde o ONNX roda. DirectML usa qualquer GPU DX12 (NVIDIA, AMD, Intel).</summary>
public enum OnnxDevice { Cpu, DirectML }

/// <summary>
/// Classificador por transformer ENCODER (MiniLM): transforma a ação num vetor de 384
/// dimensões numa única passada e compara com os exemplos rotulados (vizinhos mais
/// próximos). Não gera texto — por isso custa milissegundos, mesmo sem GPU.
/// Aprender = <see cref="AddExample"/>: cada decisão do usuário vira um exemplo novo,
/// sem re-treino — e, com <paramref name="exampleStore"/>, fica salvo no perfil local (volta ao recarregar o gate).
/// </summary>
public sealed class OnnxRiskClassifier(OnnxModelStore store, OnnxDevice device, IEnumerable<RiskExample>? seed = null,
    RiskExampleStore? exampleStore = null) : IRiskClassifier
{
    private const int MaxTokens = 128;
    private const int TopK = 3;

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private bool _needsTokenTypes;
    private readonly List<(float[] Vector, RiskExample Example)> _memory = new();
    private readonly IEnumerable<RiskExample> _seed = seed ?? RiskExamples.Seed;

    public string Name => device == OnnxDevice.DirectML ? "ONNX GPU (DirectML)" : "ONNX CPU";

    public int ExampleCount => _memory.Count;

    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (!store.IsInstalled)
            throw new InvalidOperationException($"Modelo ONNX não encontrado em {store.Folder}. Baixe-o primeiro.");

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (device == OnnxDevice.DirectML)
        {
            // Exigências do executor DirectML: sem memory pattern e execução sequencial.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        }

        _session = new InferenceSession(store.ModelPath, options);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _tokenizer = BertTokenizer.Create(store.VocabPath);

        // Semente + o que a pessoa ensinou (a mesma ação: vale o último rótulo). Benchmarks não passam exampleStore,
        // para o conjunto de referência continuar independente.
        var examples = _seed.Concat(exampleStore?.Load() ?? Array.Empty<RiskExample>())
            .GroupBy(e => e.Action, StringComparer.Ordinal).Select(g => g.Last());
        foreach (var ex in examples)
        {
            ct.ThrowIfCancellationRequested();
            _memory.Add((Embed(ex.Action), ex));
        }
    }, ct);

    /// <summary>Ensina um exemplo novo (ex.: decisão aprovada/negada pelo usuário).</summary>
    public void AddExample(RiskExample example)
    {
        example = RiskExampleStore.Validate(example);
        var v = Embed(example.Action);
        lock (_memory)
        {
            // Só vale depois de gravado (erro de disco volta para a tela); a mesma ação troca de rótulo, sem duplicar.
            exampleStore?.Upsert(example);
            _memory.RemoveAll(m => m.Example.Action == example.Action);
            _memory.Add((v, example));
        }
    }

    public Task<RiskResult> ClassifyAsync(string action, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var v = Embed(action);

        // Cópia sob trava: o usuário pode ensinar um exemplo enquanto o gate classifica.
        (float[] Vector, RiskExample Example)[] memory;
        lock (_memory) memory = _memory.ToArray();

        // Pontuação por classe = média das TopK maiores similaridades daquela classe.
        var scores = memory
            .GroupBy(m => m.Example.Level)
            .Select(g => (Level: g.Key, Score: g.Select(m => Dot(v, m.Vector)).OrderByDescending(s => s).Take(TopK).Average(),
                          Nearest: g.MaxBy(m => Dot(v, m.Vector)).Example.Action))
            .OrderByDescending(s => s.Score)
            .ToList();

        // Softmax com temperatura baixa: diferenças pequenas de cosseno viram confiança legível.
        var exp = scores.Select(s => Math.Exp(s.Score / 0.05)).ToList();
        var confidence = exp[0] / exp.Sum();
        var best = scores[0];
        return Task.FromResult(new RiskResult(best.Level, confidence, Name,
            NxText.T($"mais parecido com: {best.Nearest}", $"closest to: {best.Nearest}", $"más parecido a: {best.Nearest}"), sw.Elapsed));
    }

    /// <summary>Tokeniza, roda o MiniLM e faz mean pooling + normalização L2.</summary>
    private float[] Embed(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _);
        var len = ids.Count;
        var inputIds = new DenseTensor<long>(new[] { 1, len });
        var mask = new DenseTensor<long>(new[] { 1, len });
        var types = new DenseTensor<long>(new[] { 1, len });
        for (var i = 0; i < len; i++)
        {
            inputIds[0, i] = ids[i];
            mask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_needsTokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        using var outputs = _session!.Run(inputs);
        var hidden = outputs[0].AsTensor<float>(); // [1, len, 384]
        var dim = hidden.Dimensions[2];
        var vec = new float[dim];
        for (var t = 0; t < len; t++)
            for (var d = 0; d < dim; d++)
                vec[d] += hidden[0, t, d];

        double norm = 0;
        for (var d = 0; d < dim; d++) { vec[d] /= len; norm += vec[d] * vec[d]; }
        var inv = (float)(1 / Math.Sqrt(norm));
        for (var d = 0; d < dim; d++) vec[d] *= inv;
        return vec;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public void Dispose() => _session?.Dispose();
}
