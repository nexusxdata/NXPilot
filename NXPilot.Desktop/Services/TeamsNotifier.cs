// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using NXPilot.Desktop.ViewModels;
using NXPilot.Integrations;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Extensão Teams: os avisos do NXPilot escolhidos na extensão (decisão pendente, tarefa, falha,
/// mensagem dos Canais) vão também para o canal do Teams. Só o título e o texto curto do aviso —
/// nunca código, diff ou o conteúdo dos arquivos. Falha no envio não atrapalha o app.
/// </summary>
public static class TeamsNotifier
{
    /// <summary>Último erro de envio (mostrado na extensão); nulo quando o último deu certo.</summary>
    public static string? LastError { get; private set; }

    private static Func<string?>? _currentSubject;

    /// <param name="currentSubject">Pasta do assunto da tarefa em andamento (nula em repositório git).</param>
    public static void Attach(NotificationCenter center, Func<string?> currentSubject)
    {
        _currentSubject = currentSubject;
        center.Raised += item => _ = ForwardAsync(item);
    }

    /// <summary>"NXPilot › testePilot01": o assunto com o agrupador (a pasta onde ele está).</summary>
    public static string GroupedName(string subjectPath)
    {
        var path = subjectPath.TrimEnd('\\', '/');
        var group = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(group) ? System.IO.Path.GetFileName(path) : $"{group} › {System.IO.Path.GetFileName(path)}";
    }

    public static string KindLabel(NotificationKind kind) => kind switch
    {
        NotificationKind.Decision => T("Decisão pendente", "Pending decision", "Decisión pendiente"),
        NotificationKind.Task => T("Tarefa", "Task", "Tarea"),
        NotificationKind.Failure => T("Falha", "Failure", "Error"),
        _ => T("Mensagem", "Message", "Mensaje"),
    };

    /// <summary>O que vai para o canal (a extensão mostra o mesmo formato antes de enviar).</summary>
    public static TeamsMessage MessageFor(NotificationKind kind, string title, string body, string? subjectPath = null)
    {
        var facts = new List<(string, string)> { (T("Tipo", "Type", "Tipo"), KindLabel(kind)) };
        if (subjectPath != null) facts.Add((T("Assunto", "Subject", "Asunto"), GroupedName(subjectPath)));
        facts.Add((T("Quando", "When", "Cuándo"), DateTime.Now.ToString("dd/MM HH:mm")));
        return new($"NXPilot · {title}", body, facts);
    }

    private static async Task ForwardAsync(NotificationItem item)
    {
        try
        {
            var s = TeamsSettings.Load();
            if (!s.IsConfigured) return;
            var wanted = item.Kind switch
            {
                NotificationKind.Decision => s.SendDecisions,
                NotificationKind.Task => s.SendTasks,
                NotificationKind.Failure => s.SendFailures,
                _ => s.SendMessages,
            };
            if (!wanted) return;
            // Mensagens dos Canais não são de uma tarefa: sem assunto e sem filtro de assunto.
            var subject = item.Kind == NotificationKind.Message ? null : _currentSubject?.Invoke();
            if (!s.AcceptsSubject(subject)) return;
            using var client = new TeamsWebhookClient(s.WebhookUrl);
            await client.SendAsync(MessageFor(item.Kind, item.Title, item.Body, subject));
            LastError = null;
        }
        catch (Exception ex) { LastError = $"{DateTime.Now:dd/MM HH:mm} · {ex.Message}"; }
    }
}
