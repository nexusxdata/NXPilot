// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NXPilot.Ai;
using NXPilot.Benchmark;
using NXPilot.Llama;
using NXPilot.Onnx;
using NXPilot.Desktop.Services;
using NXPilot.Risk;
using static NXPilot.NxText;

namespace NXPilot.Desktop.ViewModels;

/// <summary>
/// Laboratório IA: escolhe onde o gate roda (CPU padrão / GPU opcional / cascata com LLaMA),
/// compara os motores lado a lado e permite testar e ENSINAR o classificador ONNX.
/// </summary>
public sealed partial class AiLabViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _suppressRebuild;

    public AiLabViewModel(MainViewModel main)
    {
        _main = main;
        var llama = new LlamaRiskClassifier();
        LlamaFolder = llama.Folder;
        LlamaInstalled = llama.IsInstalled;
        _llamaModel = llama.IsInstalled ? $"{llama.ModelFile} · backend {llama.Backend} · {T("licença", "license", "licencia")}: {LlamaRiskClassifier.LicenseOf(llama.ModelFile).License}" : null;
        // Textos calculados (Info, provedor) acompanham a troca de idioma.
        LanguageService.LanguageChanged += () => OnPropertyChanged(string.Empty);
        Provider = AiProviderSettings.Load();

        // A cascata é o pilar do produto: ligada sempre que houver um LLM para o 3º degrau.
        // Campo direto (não a propriedade) para não recarregar o gate durante a construção.
        _useCascade = CanCascade;
    }

    /// <summary>Provedor do 3º degrau (LLaMA local, Copilot, Azure, M365, OpenRouter).</summary>
    public AiProviderSettings Provider { get; }

    public string ProviderName => AiProviderSettings.DisplayName(Provider.Kind);

    public string ProviderInfo => Provider.IsRemote
        ? $"{ProviderName} · {(Provider.Model.Length > 0 ? Provider.Model : T("deployment da empresa", "company deployment", "deployment de la empresa"))} — " + T("ações em dúvida saem da máquina", "doubtful actions leave this machine", "las acciones dudosas salen de esta máquina")
        : LlamaInfo;

    /// <summary>Há LLM para o 3º degrau: LLaMA instalado ou provedor remoto configurado.</summary>
    public bool CanCascade => Provider.IsRemote || LlamaInstalled;

    /// <summary>A janela de provedor salvou: atualiza a tela e remonta o gate com o novo 3º degrau.</summary>
    public void ProviderSaved()
    {
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(ProviderInfo));
        OnPropertyChanged(nameof(CanCascade));
        _main.AddLog(T("Laboratório", "Lab", "Laboratorio"), "info", T($"3º degrau da cascata: {ProviderName}.", $"Cascade step 3: {ProviderName}.", $"Paso 3 de la cascada: {ProviderName}."));
        if (UseCascade) RebuildGate();
        else UseCascade = CanCascade; // dispara a recarga pelo OnUseCascadeChanged
    }

    public string OnnxFolder => _main.OnnxStore.Folder;
    public bool OnnxInstalled => _main.OnnxStore.IsInstalled;
    public string OnnxInfo => OnnxInstalled ? "all-MiniLM-L6-v2 · 90 MB · encoder (BERT)" : T("não baixado", "not downloaded", "no descargado");

    public string LlamaFolder { get; }
    public bool LlamaInstalled { get; }
    private readonly string? _llamaModel;
    public string LlamaInfo => _llamaModel ?? T("não instalada (instale pelo NXProject → IA → Gerenciar)", "not installed (install it from NXProject → AI → Manage)", "no instalada (instálela desde NXProject → IA → Administrar)");

    /// <summary>Gate em GPU (DirectML). Desligado por padrão: CPU funciona em qualquer máquina.</summary>
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private bool _useCascade;
    [ObservableProperty] private bool _gpuUnavailable;

    [ObservableProperty] private bool _benchGpu = true;
    [ObservableProperty] private bool _benchLlama = true;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = T("Pronto.", "Ready.", "Listo.");
    [ObservableProperty] private string? _insight;

    public ObservableCollection<BenchmarkRow> Results { get; } = new();
    [ObservableProperty] private BenchmarkRow? _selectedResult;

    [ObservableProperty] private string _tryText = "Bash: gcloud projects delete nx-producao --quiet";
    public ObservableCollection<TryRow> TryResults { get; } = new();

    partial void OnUseGpuChanged(bool value) => RebuildGate();
    partial void OnUseCascadeChanged(bool value) => RebuildGate();

    private async void RebuildGate()
    {
        if (_suppressRebuild) return;
        IsBusy = true;
        Status = T("Recarregando o gate…", "Reloading the gate…", "Recargando el gate…");
        try { await _main.RebuildGateAsync(); Status = _main.GateStatus; }
        catch (Exception ex) { Status = T("Falha ao montar o gate: ", "Could not build the gate: ", "No se pudo montar el gate: ") + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Chamado pelo gate quando o DirectML falha: desmarca a GPU sem disparar nova recarga.</summary>
    public void ReportGpuUnavailable()
    {
        _suppressRebuild = true;
        UseGpu = false;
        _suppressRebuild = false;
        GpuUnavailable = true;
    }

    [RelayCommand]
    private async Task DownloadOnnx()
    {
        IsBusy = true;
        try
        {
            await _main.OnnxStore.DownloadAsync(new Progress<string>(m => Status = m));
            OnPropertyChanged(nameof(OnnxInstalled));
            OnPropertyChanged(nameof(OnnxInfo));
            _main.AddLog(T("Laboratório", "Lab", "Laboratorio"), "info", T($"Modelo ONNX baixado em {OnnxFolder}", $"ONNX model downloaded to {OnnxFolder}", $"Modelo ONNX descargado en {OnnxFolder}"));
            await _main.RebuildGateAsync();
        }
        catch (Exception ex) { Status = T("Falha no download: ", "Download failed: ", "Falló la descarga: ") + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Instâncias NOVAS por motor (não as do gate), para que a carga entre na medição.
    /// O LLaMA reaproveita a llama.dll já carregada — a carga medida é a do modelo.
    /// </summary>
    [RelayCommand]
    private async Task RunBenchmark()
    {
        if (!OnnxInstalled) { Status = T("Baixe o modelo ONNX primeiro.", "Download the ONNX model first.", "Descargue primero el modelo ONNX."); return; }
        IsBusy = true;
        Results.Clear();
        Insight = null;
        try
        {
            var store = _main.OnnxStore;
            var engines = new List<Func<IRiskClassifier>>
            {
                () => new RuleRiskClassifier(),
                () => new OnnxRiskClassifier(store, OnnxDevice.Cpu),
            };
            if (BenchGpu) engines.Add(() => new OnnxRiskClassifier(store, OnnxDevice.DirectML));
            engines.Add(() => new HybridRiskClassifier(new RuleRiskClassifier(), new OnnxRiskClassifier(store, OnnxDevice.Cpu)));
            if (BenchLlama && LlamaInstalled)
            {
                engines.Add(() => new LlamaRiskClassifier());
                engines.Add(() => new CascadeRiskClassifier(new RuleRiskClassifier(), new OnnxRiskClassifier(store, OnnxDevice.Cpu), new LlamaRiskClassifier()));
            }

            var progress = new Progress<string>(m => Status = m);
            foreach (var make in engines)
            {
                using var engine = make();
                var report = await Task.Run(() => RiskBenchmark.RunAsync(engine, progress: progress));
                Results.Add(BenchmarkRow.From(report));
            }
            SelectedResult = Results.FirstOrDefault(r => r.Engine.Count(c => c == '→') == 2) ?? Results.LastOrDefault(); // a cascata (independe do idioma)
            Insight = BuildInsight();
            Status = T($"Comparação concluída · {RiskExamples.Benchmark.Count} ações que os modelos nunca viram.", $"Comparison done · {RiskExamples.Benchmark.Count} actions the models never saw.", $"Comparación concluida · {RiskExamples.Benchmark.Count} acciones que los modelos nunca vieron.");
            _main.AddLog(T("Laboratório", "Lab", "Laboratorio"), "benchmark", string.Join(" | ", Results.Select(r => $"{ShortEngine(r.Engine)}: {r.Average}, {r.Accuracy}")));
        }
        catch (Exception ex) { Status = T("Falha: ", "Failed: ", "Falló: ") + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Leitura dos números em português — a conclusão muda de máquina para máquina.</summary>
    private string BuildInsight()
    {
        var cpu = Results.FirstOrDefault(r => r.Engine == "ONNX CPU" && r.Error == null);
        var gpu = Results.FirstOrDefault(r => r.Engine.StartsWith("ONNX GPU"));
        var llm = Results.FirstOrDefault(r => r.Engine.StartsWith("LLaMA") && r.Error == null);
        var parts = new List<string>();
        if (cpu != null && llm != null)
        {
            var times = llm.AverageMs / Math.Max(cpu.AverageMs, 0.01);
            parts.Add(T($"ONNX na CPU foi {times:N0}× mais rápido que o LLaMA por ação.", $"ONNX on CPU was {times:N0}× faster than LLaMA per action.", $"ONNX en la CPU fue {times:N0}× más rápido que LLaMA por acción."));
        }
        if (gpu != null && gpu.Error != null)
            parts.Add(T("GPU (DirectML) indisponível nesta máquina — tudo bem: o padrão é CPU.", "GPU (DirectML) unavailable on this machine — that is fine: CPU is the default.", "GPU (DirectML) no disponible en esta máquina — no hay problema: el valor predeterminado es CPU."));
        else if (cpu != null && gpu != null)
            parts.Add(gpu.AverageMs > cpu.AverageMs
                ? T($"Com um modelo deste tamanho a CPU ganhou da GPU ({cpu.Average} × {gpu.Average}): enviar dados à placa custa mais que o cálculo. GPU compensa em modelos maiores ou em lote.",
                    $"With a model this size the CPU beat the GPU ({cpu.Average} × {gpu.Average}): sending data to the card costs more than the math. GPU pays off with larger models or batches.",
                    $"Con un modelo de este tamaño la CPU le ganó a la GPU ({cpu.Average} × {gpu.Average}): enviar datos a la tarjeta cuesta más que el cálculo. La GPU compensa con modelos más grandes o en lote.")
                : T($"A GPU ganhou da CPU ({gpu.Average} × {cpu.Average}).", $"The GPU beat the CPU ({gpu.Average} × {cpu.Average}).", $"La GPU le ganó a la CPU ({gpu.Average} × {cpu.Average})."));
        parts.Add(T("Nenhum motor aprova sozinho o que não for BAIXO — erro de classificação vira pergunta ao usuário, não ação perigosa.",
                  "No engine auto-approves anything that is not LOW — a classification error becomes a question to the user, not a dangerous action.",
                  "Ningún motor aprueba por sí solo lo que no sea BAJO — un error de clasificación se convierte en una pregunta al usuario, no en una acción peligrosa."));
        return string.Join("  ", parts);
    }

    [RelayCommand]
    private async Task TryClassify()
    {
        if (string.IsNullOrWhiteSpace(TryText)) return;
        TryResults.Clear();
        var text = TryText.Trim();
        var engines = new List<IRiskClassifier> { _main.Rules };
        if (_main.GateOnnx != null) engines.Add(_main.GateOnnx);
        if (_main.GateSlow != null) engines.Add(_main.GateSlow);

        foreach (var e in engines)
        {
            var r = await Task.Run(() => e.ClassifyAsync(text));
            TryResults.Add(new TryRow(e.Name, r.Level, $"{r.Confidence:P0}", BenchmarkRow.FormatMs(r.Elapsed.TotalMilliseconds), r.Reason));
        }
        if (_main.GateOnnx == null) Status = T("Só Regras: baixe o modelo ONNX para comparar.", "Rules only: download the ONNX model to compare.", "Solo Reglas: descargue el modelo ONNX para comparar.");
        else if (_main.GateSlow == null) Status = T("Ligue a cascata para incluir o 3º degrau (LLM) no teste.", "Turn the cascade on to include step 3 (LLM) in the test.", "Active la cascada para incluir el paso 3 (LLM) en la prueba.");
    }

    /// <summary>
    /// Ensina o ONNX: a ação vira exemplo rotulado na hora, sem re-treino, e fica salva neste computador
    /// (volta ao recarregar o gate ou reabrir o app). Falha ao gravar = nada muda, e a tela diz o motivo.
    /// </summary>
    [RelayCommand]
    private async Task Teach(string level)
    {
        if (_main.GateOnnx == null || string.IsNullOrWhiteSpace(TryText)) return;
        var lvl = Enum.Parse<RiskLevel>(level);
        var text = TryText.Trim();
        try { await Task.Run(() => _main.GateOnnx.AddExample(new RiskExample(text, lvl))); }
        catch (Exception ex)
        {
            Status = T("Não foi possível salvar o exemplo: ", "Could not save the example: ", "No se pudo guardar el ejemplo: ") + ex.Message;
            return;
        }
        _main.AddLog(T("Você", "You", "Usted"), T("ensinou", "taught", "enseñó"), T($"{text} → {lvl} ({_main.GateOnnx.ExampleCount} exemplos)", $"{text} → {lvl} ({_main.GateOnnx.ExampleCount} examples)", $"{text} → {lvl} ({_main.GateOnnx.ExampleCount} ejemplos)"));
        await TryClassify();
        Status = T($"Aprendido e salvo neste computador: \"{text}\" = {lvl}. Ações parecidas agora tendem a {lvl}.",
            $"Learned and saved on this computer: \"{text}\" = {lvl}. Similar actions now lean to {lvl}.",
            $"Aprendido y guardado en este equipo: \"{text}\" = {lvl}. Las acciones parecidas ahora tienden a {lvl}.");
    }

    public static string ShortEngine(string engine) =>
        System.Text.RegularExpressions.Regex.Replace(engine, @" \([^)]*\.gguf\)", "").Replace("LLaMA CPU", "LLaMA").Replace("ONNX CPU → LLaMA", "ONNX → LLaMA");
}
