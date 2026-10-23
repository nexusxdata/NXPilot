// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;

namespace NXPilot.Risk;

/// <summary>
/// Exemplos ensinados pela pessoa no Laboratório IA, guardados no perfil local
/// (%LOCALAPPDATA%\NXPilot\risk-examples.json) — separados da semente e dos benchmarks. Sem isto, recarregar o gate
/// (ou trocar CPU/GPU, ou reabrir o app) perdia o aprendizado. Gravação atômica (arquivo temporário trocado no fim);
/// a mesma ação ensinada de novo substitui o rótulo anterior. Contribuição de @alannascimento-svg (PR #2).
/// </summary>
public sealed class RiskExampleStore(string? filePath = null)
{
    private static readonly object Sync = new();

    public string FilePath { get; } = Path.GetFullPath(filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "risk-examples.json"));

    /// <summary>Exemplos salvos (vazio sem arquivo). Arquivo inválido = <see cref="InvalidDataException"/> — e ele é preservado.</summary>
    public IReadOnlyList<RiskExample> Load()
    {
        lock (Sync) return Read();
    }

    /// <summary>Grava (ou troca o rótulo de) uma ação ensinada.</summary>
    public void Upsert(RiskExample example)
    {
        example = Validate(example);
        lock (Sync)
        {
            var examples = Read().Where(e => e.Action != example.Action).Append(example).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, examples, new JsonSerializerOptions { WriteIndented = true });
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private IReadOnlyList<RiskExample> Read()
    {
        if (!File.Exists(FilePath)) return Array.Empty<RiskExample>();
        try
        {
            var examples = JsonSerializer.Deserialize<RiskExample[]>(File.ReadAllText(FilePath))
                ?? throw new JsonException("Expected an array of risk examples.");
            return examples.Select(Validate).GroupBy(e => e.Action, StringComparer.Ordinal).Select(g => g.Last()).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException(NxText.T($"Não foi possível ler os exemplos ensinados: {FilePath}", $"Could not read the taught examples: {FilePath}",
                $"No se pudieron leer los ejemplos enseñados: {FilePath}"), ex);
        }
    }

    public static RiskExample Validate(RiskExample example)
    {
        ArgumentNullException.ThrowIfNull(example);
        ArgumentException.ThrowIfNullOrWhiteSpace(example.Action);
        if (!Enum.IsDefined(example.Level)) throw new ArgumentOutOfRangeException(nameof(example));
        return example with { Action = example.Action.Trim() };
    }
}
