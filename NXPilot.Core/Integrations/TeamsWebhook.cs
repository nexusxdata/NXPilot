// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.Ai;

namespace NXPilot.Integrations;

/// <summary>Formato do webhook do canal do Teams, deduzido da URL.</summary>
public enum TeamsWebhookKind
{
    /// <summary>Workflows/Power Automate ("Post to a channel when a webhook request is received"): Adaptive Card.</summary>
    Workflows,
    /// <summary>Incoming Webhook antigo (conectores do Office 365, *.webhook.office.com): MessageCard.</summary>
    IncomingWebhook,
}

/// <summary>
/// Extensão Teams: URL do webhook de um canal (cifrada — quem tem a URL posta no canal) e quais
/// avisos do NXPilot vão para lá. %LOCALAPPDATA%\NXPilot\teams.json.
/// </summary>
public sealed class TeamsSettings
{
    [JsonIgnore] public string WebhookUrl { get; set; } = "";
    public string WebhookUrlProtected { get; set; } = "";

    public bool SendDecisions { get; set; } = true;
    public bool SendTasks { get; set; } = true;
    public bool SendFailures { get; set; } = true;
    public bool SendMessages { get; set; }

    /// <summary>
    /// Assuntos (pastas) cujos avisos vão para o canal; vazio = todos. Tarefas em repositório git
    /// (sem assunto) e mensagens dos Canais não passam por este filtro.
    /// </summary>
    public List<string> Subjects { get; set; } = new();

    /// <summary>O aviso de uma tarefa deste assunto vai para o canal?</summary>
    public bool AcceptsSubject(string? subjectPath) =>
        Subjects.Count == 0 || subjectPath == null || Subjects.Any(s => string.Equals(s.TrimEnd('\\', '/'), subjectPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

    public bool IsConfigured => WebhookUrl.Length > 0;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "teams.json");

    [SupportedOSPlatform("windows")]
    public static TeamsSettings Load()
    {
        var s = new TeamsSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<TeamsSettings>(File.ReadAllText(FilePath)) is { } saved) s = saved;
        }
        catch { /* arquivo inválido: começa vazio */ }
        s.WebhookUrl = Secret.Unprotect(s.WebhookUrlProtected);
        return s;
    }

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        WebhookUrlProtected = Secret.Protect(WebhookUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Um aviso para o canal: título, texto curto e pares "nome: valor".</summary>
public sealed record TeamsMessage(string Title, string Text, IReadOnlyList<(string Name, string Value)>? Facts = null);

/// <summary>
/// Posta no canal do Teams pelo webhook. A URL já carrega a autorização (o sig= do Workflows ou o
/// caminho do Incoming Webhook): um POST, sem login. Só texto curto — nunca código nem diff.
/// </summary>
public sealed class TeamsWebhookClient(string webhookUrl, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

    /// <summary>Workflows (logic.azure.com / powerplatform.com…) ou Incoming Webhook (webhook.office.com).</summary>
    public static TeamsWebhookKind KindOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("outlook.office.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/webhook", StringComparison.OrdinalIgnoreCase))
            ? TeamsWebhookKind.IncomingWebhook : TeamsWebhookKind.Workflows;

    /// <summary>Só https: a URL é a própria credencial, não pode ir em texto aberto.</summary>
    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Corpo do POST no formato que aquele tipo de webhook aceita.</summary>
    public static string Payload(TeamsWebhookKind kind, TeamsMessage message)
    {
        var facts = message.Facts ?? Array.Empty<(string, string)>();
        object body = kind == TeamsWebhookKind.IncomingWebhook
            ? new Dictionary<string, object?>
            {
                ["@type"] = "MessageCard",
                ["@context"] = "https://schema.org/extensions",
                ["summary"] = message.Title,
                ["themeColor"] = "2B579A",
                ["title"] = message.Title,
                ["text"] = message.Text,
                ["sections"] = facts.Count == 0 ? null : new[] { new { facts = facts.Select(f => new { name = f.Name, value = f.Value }).ToArray() } },
            }
            : new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = new Dictionary<string, object?>
                        {
                            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                            ["type"] = "AdaptiveCard",
                            ["version"] = "1.4",
                            ["body"] = new object[]
                            {
                                new { type = "TextBlock", text = message.Title, weight = "Bolder", size = "Medium", wrap = true },
                                new { type = "TextBlock", text = message.Text, wrap = true },
                            }.Concat(facts.Count == 0 ? Array.Empty<object>() : new object[]
                            {
                                new { type = "FactSet", facts = facts.Select(f => new { title = f.Name, value = f.Value }).ToArray() },
                            }).ToArray(),
                        },
                    },
                },
            };
        // Acentos e "›" em UTF-8 direto (o corpo vai como application/json; charset=utf-8), sem \uXXXX.
        return JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public async Task SendAsync(TeamsMessage message, CancellationToken ct = default)
    {
        if (!IsValidUrl(webhookUrl)) throw new InvalidOperationException(NxText.T("A URL do webhook precisa começar com https://.", "The webhook URL must start with https://.", "La URL del webhook debe comenzar con https://."));
        using var resp = await _http.PostAsync(webhookUrl.Trim(), new StringContent(Payload(KindOf(webhookUrl), message), Encoding.UTF8, "application/json"), ct);
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync(ct);
        // A URL nunca entra na mensagem de erro: ela é a credencial do canal.
        throw new InvalidOperationException($"Teams {(int)resp.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
    }

    public void Dispose() => _http.Dispose();
}
