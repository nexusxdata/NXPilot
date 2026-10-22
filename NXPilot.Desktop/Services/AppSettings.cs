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
    /// <summary>"+ Opções" da Nova tarefa como a pessoa deixou da última vez (aberto ou recolhido).</summary>
    public bool NewTaskOptionsExpanded { get; set; }
    /// <summary>Pasta dos assuntos do versionamento local (ex.: OneDrive); vazia = Documentos\NXPilot.</summary>
    public string? SubjectsRoot { get; set; }
    public string? LastModel { get; set; }
    public string? CodexModel { get; set; }
    public string? Reviewer { get; set; }
    public string? Executor { get; set; }
    /// <summary>MCP NXPilot parado em Configurar: continua parado ao reabrir o app (padrão: ligado).</summary>
    public bool McpStopped { get; set; }

    /// <summary>Regras do plano (fim de todo pedido de plano) por agente; nulo = padrão de <see cref="NXPilot.Planning.PlanRules"/>.</summary>
    public string? PlanRulesClaude { get; set; }
    public string? PlanRulesCodex { get; set; }
    public string? PlanRulesNXAgentic { get; set; }

    public string PlanRulesFor(bool codex) => PlanRulesFor(codex ? "Codex" : "Claude");

    /// <summary>Regra gravada do agente ("Claude", "Codex", "NXAgentic") ou o padrão dele.</summary>
    public string PlanRulesFor(string agent) => agent switch
    {
        "Codex" => PlanRulesCodex,
        "NXAgentic" => PlanRulesNXAgentic,
        _ => PlanRulesClaude,
    } ?? NXPilot.Planning.PlanRules.Default(agent == "Codex");

    /// <summary>Grava a regra do agente; o padrão (ou vazio) volta a ser "usar o padrão".</summary>
    public void SetPlanRules(string agent, string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) || text.Trim() == NXPilot.Planning.PlanRules.Default(agent == "Codex").Trim() ? null : text.Trim();
        switch (agent)
        {
            case "Codex": PlanRulesCodex = value; break;
            case "NXAgentic": PlanRulesNXAgentic = value; break;
            default: PlanRulesClaude = value; break;
        }
    }

    /// <summary>
    /// Padrão por assunto (chave: pasta do assunto): agente, modelos e revisor que voltam sozinhos
    /// ao escolher o assunto na Nova tarefa. Assunto sem padrão segue a última escolha.
    /// </summary>
    public Dictionary<string, SubjectDefaults> SubjectDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ferramentas: posição de cada ícone na "área de trabalho" (id → "coluna,linha").</summary>
    public Dictionary<string, string> ToolPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
