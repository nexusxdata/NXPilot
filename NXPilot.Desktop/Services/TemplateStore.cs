// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;
using NXPilot.Agents;
using NXPilot.Desktop.ViewModels;
using NXPilot.Planning;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Hook (tarefa em modo escuta): a intenção (com {texto} para o evento), as opções da tarefa, o
/// PLANO aprovado e as ações aprovadas na execução de teste. Cada evento cria uma TAREFA COMUM com
/// esse plano, num worktree novo.
/// </summary>
public sealed class TaskTemplate
{
    public string Id { get; set; } = "";
    public NewTaskInfo Info { get; set; } = null!;
    public string PlanText { get; set; } = "";
    public List<PlannedPhase> Phases { get; set; } = new();

    /// <summary>Assinaturas das ações aprovadas na execução de teste: pré-aprovadas nos disparos (inclusive ALTO, só as mesmas).</summary>
    public List<string> PreApprovals { get; set; } = new();

    /// <summary>Em modo escuta? Pausado, os eventos são ignorados (e registrados).</summary>
    public bool Armed { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ArmedAt { get; set; } = DateTime.Now;

    /// <summary>Disparo a menos disto do anterior pede aprovação (recursão).</summary>
    public int MinIntervalSeconds { get; set; } = TemplateTriggers.DefaultMinIntervalSeconds;

    /// <summary>Mais disparos que isto na última hora pede aprovação.</summary>
    public int MaxPerHour { get; set; } = TemplateTriggers.DefaultMaxPerHour;

    /// <summary>Horários dos disparos (só as últimas 24 h ficam guardadas).</summary>
    public List<DateTime> Fires { get; set; } = new();
    public int RunCount { get; set; }
    public string? LastTaskId { get; set; }

    public string Title => Info.Title;
}

/// <summary>Hooks em %LOCALAPPDATA%\NXPilot\templates\&lt;id&gt;.json.</summary>
public static class TemplateStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "templates");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<TaskTemplate> LoadAll()
    {
        if (!Directory.Exists(Dir)) return new();
        var list = new List<TaskTemplate>();
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<TaskTemplate>(File.ReadAllText(file), Json) is { Info: not null } t) list.Add(t); }
            catch { /* arquivo inválido: ignora */ }
        }
        return list.OrderByDescending(t => t.ArmedAt).ToList();
    }

    public static TaskTemplate? Load(string id) => LoadAll().FirstOrDefault(t => t.Id == id);

    public static void Save(TaskTemplate template)
    {
        template.Fires = template.Fires.Where(f => DateTime.Now - f < TimeSpan.FromHours(24)).ToList();
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, template.Id + ".json"), JsonSerializer.Serialize(template, Json));
    }

    public static void Delete(string id)
    {
        var file = Path.Combine(Dir, id + ".json");
        if (File.Exists(file)) File.Delete(file);
    }
}
