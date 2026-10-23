// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NXPilot.Ai;

namespace NXPilot.Integrations;

/// <summary>
/// Extensão Databricks Gate Agent: o Claude Code (e depois o Codex) rodando pelo AI Gateway da Databricks
/// (https://&lt;workspace&gt;/ai-gateway/anthropic) — modelos liberados no Unity Catalog, consumo no
/// contrato da Databricks. PAT próprio (escopo ai-gateway), separado do PAT do SQL, cifrado com DPAPI.
/// O gate do NXPilot continua o mesmo: só muda onde o modelo roda.
/// </summary>
public sealed class DatabricksAgentSettings
{
    /// <summary>Workspace (https://…); vazio = o mesmo da extensão Databricks SQL.</summary>
    public string Host { get; set; } = "";
    /// <summary>Modelo Claude (nome no Unity Catalog, como o gateway lista).</summary>
    public string ClaudeModel { get; set; } = "";
    /// <summary>Modelo rápido que o Claude Code usa nas tarefas de fundo (o Haiku mais novo do gateway).</summary>
    public string FastModel { get; set; } = "";
    /// <summary>Modelos do gateway que o Claude Code usa (nome no Unity Catalog → nome amigável), para a Nova tarefa escolher sem rede.</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    /// <summary>PAT do workspace <see cref="TokenHost"/> — o PAT é do workspace, não do modelo: trocar o modelo não mexe nele.</summary>
    [JsonIgnore] public string Token { get; set; } = "";
    public string TokenProtected { get; set; } = "";
    /// <summary>Workspace para o qual o PAT foi salvo. Outro workspace = sem PAT até colar o dele.</summary>
    public string TokenHost { get; set; } = "";

    [JsonIgnore] public bool IsComplete => EffectiveHost.Length > "https://".Length && Token.Length > 0 && ClaudeModel.Length > 0;

    /// <summary>Host salvo ou, vazio, o da extensão Databricks SQL.</summary>
    [JsonIgnore] public string EffectiveHost { get; set; } = "";

    internal static string FilePath => ExtensionSettings.File("databricks-agent.json");

    [SupportedOSPlatform("windows")]
    public static DatabricksAgentSettings Load()
    {
        var s = new DatabricksAgentSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<DatabricksAgentSettings>(File.ReadAllText(FilePath)) is { } saved)
            {
                s = saved;
                s.Token = Secret.Unprotect(s.TokenProtected);
            }
        }
        catch { /* arquivo inválido: começa vazio */ }
        s.EffectiveHost = DatabricksAgent.NormalizeHost(s.Host.Length > 0 ? s.Host : DatabricksSettings.Load().Host);
        if (s.TokenHost.Length == 0 && s.Token.Length > 0) s.TokenHost = s.EffectiveHost; // arquivo de antes: o PAT era deste workspace
        if (!string.Equals(DatabricksAgent.NormalizeHost(s.TokenHost), s.EffectiveHost, StringComparison.OrdinalIgnoreCase)) s.Token = ""; // workspace mudou
        return s;
    }

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        TokenProtected = Secret.Protect(Token);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Modelo do gateway: nome no Unity Catalog (vai para o agente), nome amigável (vai para a tela) e as APIs que ele
/// atende (api_types). Nulo = veio da rota da Anthropic, onde todos falam o protocolo do Claude Code.
/// </summary>
public sealed record GatewayModel(string Id, string Name, IReadOnlyList<string>? ApiTypes = null)
{
    /// <summary>Protocolo que o Claude Code usa: só os modelos que o atendem servem para o Gate Databricks.</summary>
    public const string AnthropicApi = "anthropic/v1/messages";

    public bool ForClaudeCode => ApiTypes == null || ApiTypes.Contains(AnthropicApi);

    /// <summary>Por que não serve para o Gate Databricks (nulo = serve) — aparece ao lado do nome, desabilitado.</summary>
    public string? WhyNot => ForClaudeCode ? null
        : ApiTypes!.Count > 0 && ApiTypes.All(a => a.EndsWith("embeddings", StringComparison.OrdinalIgnoreCase))
            ? NxText.T("embeddings — não conversa", "embeddings — does not chat", "embeddings: no conversa")
            : NxText.T("não fala a API da Anthropic (a que o Claude Code usa)", "does not speak the Anthropic API (the one Claude Code uses)", "no habla la API de Anthropic (la que usa Claude Code)");

    public override string ToString() => Name == Id ? Id : $"{Name} — {Id}";
}

public static partial class DatabricksAgent
{
    /// <summary>Caminho do gateway compatível com a API da Anthropic (o Claude Code fala com ele).</summary>
    public const string AnthropicPath = "/ai-gateway/anthropic";

    /// <summary>
    /// Colaram o ID do token (64 hexadecimais, o que a lista "Tokens de acesso" mostra) em vez do token
    /// (começa com dapi e só aparece uma vez, ao gerar)? Erro comum — a Databricks responde 401 em tudo.
    /// </summary>
    public static bool LooksLikeTokenId(string value) => System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), "^[0-9a-f]{64}$");

    public static string TokenIdHint => NxText.T(
        "Isto é o ID do token (o código que a lista de tokens mostra), não o token. O token começa com dapi e só aparece uma vez, ao gerar — se não o tiver mais, gere um novo.",
        "This is the token ID (the code the token list shows), not the token. The token starts with dapi and is shown only once, when generated — if you no longer have it, generate a new one.",
        "Esto es el ID del token (el código que muestra la lista de tokens), no el token. El token empieza con dapi y solo aparece una vez, al generarlo; si ya no lo tiene, genere uno nuevo.");

    public static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('/');
        if (host.Length > 0 && !host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
        return host;
    }

    /// <summary>
    /// TODOS os modelos que o gateway libera para este PAT (GET /ai-gateway/v1/models, com as APIs de cada um);
    /// se essa lista não vier, os da rota da Anthropic. Erro em texto simples — principalmente PAT sem o escopo ai-gateway.
    /// </summary>
    public static async Task<List<GatewayModel>> ListModelsAsync(string host, string token, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        host = NormalizeHost(host);
        if (host.Length <= "https://".Length || token.Length == 0)
            throw new InvalidOperationException(NxText.T("Preencha o workspace e o PAT.", "Fill in the workspace and the PAT.", "Complete el workspace y el PAT."));
        if (LooksLikeTokenId(token)) throw new InvalidOperationException(TokenIdHint);
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(40);
        async Task<(HttpStatusCode Status, string Body)> Get(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, host + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Headers.Add("x-databricks-use-coding-agent-mode", "true");
            using var resp = await http.SendAsync(req, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        var (status, body) = await Get("/ai-gateway/v1/models");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidOperationException(Explain(status, body));
        if ((int)status is >= 200 and < 300 && ParseModels(body) is { Count: > 0 } all) return all;
        (status, body) = await Get(AnthropicPath + "/v1/models");
        if ((int)status is < 200 or >= 300) throw new InvalidOperationException(Explain(status, body));
        return ParseModels(body);
    }

    /// <summary>
    /// Lista geral do gateway ({"data":[{"query_name":…,"display_name":…,"api_types":[…]}]}), formato da Anthropic
    /// ({"data":[{"id":…}]}) ou lista simples de nomes. Ordem: os que servem para o Claude Code primeiro (do mais novo
    /// para o mais antigo; Opus, Sonnet, Haiku na mesma versão), depois os outros por nome.
    /// </summary>
    public static List<GatewayModel> ParseModels(string body)
    {
        var root = JsonNode.Parse(body);
        var items = root as JsonArray ?? (root as JsonObject)?["data"] as JsonArray ?? (root as JsonObject)?["models"] as JsonArray ?? [];
        return items.Select(i =>
            {
                if (i is JsonValue v) return new GatewayModel(v.ToString(), v.ToString());
                if ((i?["query_name"] ?? i?["id"] ?? i?["name"])?.GetValue<string>() is not { Length: > 0 } id) return null;
                var name = i!["display_name"]?.GetValue<string>() is { Length: > 0 } d ? d : id;
                var apis = i["api_types"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() : null;
                return new GatewayModel(id, name, apis);
            })
            .Where(m => m != null).Select(m => m!).DistinctBy(m => m.Id)
            .OrderByDescending(m => m.ForClaudeCode).ThenByDescending(m => Version(m.Id)).ThenBy(m => Family(m.Id)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Padrão quando nada foi escolhido: o Sonnet mais novo (equilíbrio entre qualidade e custo para agente de código);
    /// sem Sonnet, o primeiro Claude da lista.
    /// </summary>
    public static GatewayModel? DefaultModel(IReadOnlyList<GatewayModel> models) =>
        models.FirstOrDefault(m => m.ForClaudeCode && Family(m.Id) == 1) ?? models.FirstOrDefault(m => m.ForClaudeCode);

    /// <summary>Modelo rápido (tarefas de fundo do Claude Code): o Haiku mais novo.</summary>
    public static GatewayModel? FastModel(IReadOnlyList<GatewayModel> models) => models.FirstOrDefault(m => m.ForClaudeCode && Family(m.Id) == 2);

    /// <summary>0 Opus, 1 Sonnet, 2 Haiku, 3 outro.</summary>
    private static int Family(string id) =>
        id.Contains("opus", StringComparison.OrdinalIgnoreCase) ? 0 : id.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ? 1
        : id.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    /// <summary>Versão no fim do nome ("claude-opus-4-5" → 4.5, "claude-sonnet-5" → 5.0); outro modelo = -1.</summary>
    private static double Version(string id)
    {
        if (Family(id) == 3) return -1;
        var m = VersionTail().Match(id);
        return m.Success ? int.Parse(m.Groups[1].Value) + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) / 10.0 : 0) : 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-(\d+)(?:-(\d))?$")] private static partial System.Text.RegularExpressions.Regex VersionTail();

    /// <summary>
    /// O PAT faz mais do que o agente precisa? Pergunta a uma API comum do workspace (listar a raiz): com o PAT
    /// só ai-gateway a Databricks recusa por escopo; se responde, o PAT vale para outras APIs (ex.: "todas as APIs").
    /// Nulo = não deu para saber (rede, permissão do usuário) — sem aviso.
    /// </summary>
    public static async Task<bool?> HasExtraAccessAsync(string host, string token, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, NormalizeHost(host) + "/api/2.0/workspace/list?path=%2F");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return true;
            var body = await resp.Content.ReadAsStringAsync(ct);
            // A Databricks recusa um PAT de escopo restrito de dois jeitos: 403 citando o escopo, ou 401 "unsupported type for this API".
            return body.Contains("required scopes", StringComparison.OrdinalIgnoreCase)
                || body.Contains("unsupported type for this API", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch { return null; }
    }

    /// <summary>Uma chamada mínima ao modelo (1 token), como o Claude Code faz: confirma que o PAT serve para o agente.</summary>
    public static async Task CallModelAsync(string host, string token, string model, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        using var req = new HttpRequestMessage(HttpMethod.Post, NormalizeHost(host) + AnthropicPath + "/v1/messages")
        {
            Content = new StringContent(new JsonObject
            {
                ["model"] = model, ["max_tokens"] = 1,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "OK" }),
            }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("x-databricks-use-coding-agent-mode", "true");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Explain(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Recusa do gateway em linguagem simples.</summary>
    public static string Explain(HttpStatusCode status, string body)
    {
        if (body.Contains("required scopes", StringComparison.OrdinalIgnoreCase) && body.Contains("ai-gateway", StringComparison.OrdinalIgnoreCase))
            return NxText.T("O PAT não tem o escopo ai-gateway. Crie um PAT novo na Databricks (Configurações → Desenvolvedor → Tokens de acesso) com o escopo ai-gateway e cole aqui.",
                "The PAT lacks the ai-gateway scope. Create a new PAT in Databricks (Settings → Developer → Access tokens) with the ai-gateway scope and paste it here.",
                "El PAT no tiene el alcance ai-gateway. Cree un PAT nuevo en Databricks (Configuración → Desarrollador → Tokens de acceso) con el alcance ai-gateway y péguelo aquí.");
        return status switch
        {
            HttpStatusCode.Unauthorized => NxText.T("PAT recusado (vencido, inválido ou sem o escopo ai-gateway). Escopo trocado agora pode levar até 10 minutos para valer.",
                "PAT rejected (expired, invalid or without the ai-gateway scope). A scope changed just now can take up to 10 minutes to apply.",
                "PAT rechazado (vencido, no válido o sin el alcance ai-gateway). Un alcance cambiado ahora puede tardar hasta 10 minutos en aplicarse."),
            HttpStatusCode.Forbidden => NxText.T("Sem permissão no AI Gateway — peça ao admin acesso aos modelos.", "No permission on the AI Gateway — ask the admin for model access.", "Sin permiso en el AI Gateway: pida al admin acceso a los modelos.") + " " + Short(body),
            HttpStatusCode.NotFound => NxText.T("O workspace não tem o AI Gateway para agentes (rota não encontrada).", "The workspace has no AI Gateway for agents (route not found).", "El workspace no tiene el AI Gateway para agentes (ruta no encontrada)."),
            _ => $"HTTP {(int)status}: {Short(body)}",
        };
    }

    private static string Short(string body) => body.Length > 300 ? body[..300] + "…" : body;
}
