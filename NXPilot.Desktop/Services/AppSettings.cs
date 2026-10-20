// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Preferências do app num arquivo só (%LOCALAPPDATA%\NXPilot\settings.json): idioma e as
/// últimas escolhas da Nova tarefa. Ler e gravar sempre o objeto inteiro evita que salvar
/// uma preferência apague outra.
/// </summary>
/// <summary>O que o "Gravar como padrão do assunto" guarda.</summary>
public sealed record SubjectDefaults(string Executor, string? Model, string Reviewer, string? CodexModel, DateTime SavedAt);

public sealed class AppSettings
{
    public string? Language { get; set; }
    public string? LastRepo { get; set; }
    /// <summary>Versionamento da última tarefa: "Local" (repositório-sombra) ou "Git".</summary>
    public string? Versioning { get; set; }
    public string? LastSubject { get; set; }
    /// <summary>Pasta dos assuntos do versionamento local (ex.: OneDrive); vazia = Documentos\NXPilot.</summary>
    public string? SubjectsRoot { get; set; }
    public string? LastModel { get; set; }
    public string? CodexModel { get; set; }
    public string? Reviewer { get; set; }
    public string? Executor { get; set; }
    /// <summary>MCP NXPilot parado em Configurar: continua parado ao reabrir o app (padrão: ligado).</summary>
    public bool McpStopped { get; set; }

    /// <summary>
    /// Padrão por assunto (chave: pasta do assunto): agente, modelos e revisor que voltam sozinhos
    /// ao escolher o assunto na Nova tarefa. Assunto sem padrão segue a última escolha.
    /// </summary>
    public Dictionary<string, SubjectDefaults> SubjectDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* arquivo inválido: volta ao padrão */ }
        return new();
    }

    /// <summary>Carrega, aplica a mudança e grava — sem perder os outros campos.</summary>
    public static void Update(Action<AppSettings> change)
    {
        try
        {
            var s = Load();
            change(s);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* sem permissão: vale só nesta sessão */ }
    }
}
