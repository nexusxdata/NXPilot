// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NXPilot.Integrations;

namespace NXPilot.Agents;

/// <summary>Como o Claude Code fala com o modelo pelo Gate Databricks: endereço local, chave da sessão e modelos.</summary>
public sealed record ClaudeGateway(string BaseUrl, string SessionKey, string Model, string FastModel);

/// <summary>
/// Gate Databricks: repasse local (http://localhost:porta) entre o Claude Code e o AI Gateway da Databricks.
/// O PAT nunca sai do NXPilot — o agente (e os comandos que ele roda) só conhece uma chave de sessão
/// aleatória que vale aqui e só para as chamadas de modelo (/v1/…). Fora disso: recusado.
/// Um repasse por execução do app; para quando o NXPilot fecha.
/// </summary>
public static class DatabricksGatewayProxy
{
    private static HttpListener? _listener;
    private static string _baseUrl = "";
    private static readonly string SessionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object Gate = new();

    /// <summary>Cabeçalhos que não passam adiante (o repasse põe os dele: autorização e host de destino).</summary>
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Authorization", "x-api-key", "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Expect", "Proxy-Connection", "Upgrade" };

    /// <summary>
    /// Sobe o repasse (uma vez) e devolve como o Claude deve chamar. Nulo se o Databricks Gate Agent não estiver
    /// completo (workspace, PAT do workspace e modelo) — a tarefa explica e não roda.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ClaudeGateway? Start()
    {
        var s = DatabricksAgentSettings.Load();
        if (!s.IsComplete) return null;
        lock (Gate)
        {
            if (_listener is not { IsListening: true })
            {
                var port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _listener = listener;
                _baseUrl = $"http://localhost:{port}";
                _ = Task.Run(() => ServeAsync(listener));
            }
        }
        return new ClaudeGateway(_baseUrl, SessionKey, s.ClaudeModel, s.FastModel.Length > 0 ? s.FastModel : s.ClaudeModel);
    }

    /// <summary>
    /// Variáveis do processo do Claude Code: endereço local, chave da sessão (não o PAT), modelo escolhido também
    /// nos apelidos (opus/sonnet/haiku) e o modo de agente que o AI Gateway pede.
    /// </summary>
    public static Dictionary<string, string> Environment(ClaudeGateway g) => new()
    {
        ["ANTHROPIC_BASE_URL"] = g.BaseUrl,
        ["ANTHROPIC_AUTH_TOKEN"] = g.SessionKey,
        ["ANTHROPIC_MODEL"] = g.Model,
        ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = g.Model,
        ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = g.Model,
        ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = g.FastModel,
        ["ANTHROPIC_SMALL_FAST_MODEL"] = g.FastModel,
        ["ANTHROPIC_CUSTOM_HEADERS"] = "x-databricks-use-coding-agent-mode: true",
        ["CLAUDE_CODE_USE_GATEWAY"] = "1",
        ["ENABLE_PROMPT_CACHING_1H"] = "1",
        ["ENABLE_TOOL_SEARCH"] = "true",
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
    };

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [SupportedOSPlatform("windows")]
    private static async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; } // parado
            _ = Task.Run(() => ForwardAsync(ctx));
        }
    }

    /// <summary>Prefixo local da rota do Codex (API Responses): /codex/v1/… → /ai-gateway/codex/v1/….</summary>
    public const string CodexPrefix = "/codex";

    /// <summary>
    /// Só a chave da sessão e só chamadas de modelo seguem: /v1/… (Claude, API da Anthropic) e /codex/v1/… (Codex,
    /// API Responses). O resto volta 401/404 sem tocar na Databricks.
    /// </summary>
    public static bool Allowed(string? authorization, string? apiKey, string path, string sessionKey) =>
        (authorization == "Bearer " + sessionKey || apiKey == sessionKey) && IsModelPath(path) && !path.Contains("..");

    private static bool IsModelPath(string path) =>
        path.StartsWith("/v1/", StringComparison.Ordinal) || path.StartsWith(CodexPrefix + "/v1/", StringComparison.Ordinal);

    /// <summary>
    /// Pedido do Codex ajustado ao que os modelos abertos da Databricks (ex.: GPT OSS) aceitam: sem
    /// "parallel_tool_calls", que o Codex sempre manda e eles recusam. Corpo que não é JSON passa igual.
    /// </summary>
    public static byte[] CodexBody(byte[] body)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is System.Text.Json.Nodes.JsonObject o && o.Remove("parallel_tool_calls"))
                return System.Text.Encoding.UTF8.GetBytes(o.ToJsonString());
        }
        catch (System.Text.Json.JsonException) { /* não é JSON: segue como veio */ }
        return body;
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>Destino na Databricks: Codex na rota dele; o resto na rota da Anthropic.</summary>
    public static string Target(string host, string path) => path.StartsWith(CodexPrefix + "/", StringComparison.Ordinal)
        ? host + "/ai-gateway/codex" + path[CodexPrefix.Length..]
        : host + DatabricksAgent.AnthropicPath + path;

    [SupportedOSPlatform("windows")]
    private static async Task ForwardAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            if (!Allowed(req.Headers["Authorization"], req.Headers["x-api-key"], path, SessionKey))
            {
                resp.StatusCode = IsModelPath(path) ? 401 : 404;
                return;
            }
            var s = DatabricksAgentSettings.Load(); // PAT e workspace atuais (trocar em Extensões vale na hora)
            if (s.Token.Length == 0) { resp.StatusCode = 401; return; }
            using var outgoing = new HttpRequestMessage(new HttpMethod(req.HttpMethod), Target(s.EffectiveHost, path) + req.Url!.Query);
            if (req.HasEntityBody)
                outgoing.Content = path.StartsWith(CodexPrefix + "/", StringComparison.Ordinal) && req.Headers["Content-Encoding"] == null
                    ? new ByteArrayContent(CodexBody(await ReadAllAsync(req.InputStream)))
                    : new StreamContent(req.InputStream);
            foreach (string? name in req.Headers.AllKeys)
            {
                if (name == null || Hop.Contains(name)) continue;
                var value = req.Headers[name];
                if (value == null) continue;
                if (!outgoing.Headers.TryAddWithoutValidation(name, value)) outgoing.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);

            using var answer = await Http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead);
            resp.StatusCode = (int)answer.StatusCode;
            foreach (var h in answer.Headers.Concat(answer.Content.Headers))
                if (!Hop.Contains(h.Key) && !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) resp.Headers[h.Key] = string.Join(",", h.Value);
            resp.ContentType = answer.Content.Headers.ContentType?.ToString();
            resp.SendChunked = true; // respostas em fluxo (SSE) chegam ao Claude enquanto o modelo escreve
            await using var body = await answer.Content.ReadAsStreamAsync();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                await resp.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                await resp.OutputStream.FlushAsync();
            }
        }
        catch
        {
            try { resp.StatusCode = 502; } catch { /* cabeçalhos já enviados */ }
        }
        finally
        {
            try { resp.Close(); } catch { /* conexão já caiu */ }
        }
    }
}
