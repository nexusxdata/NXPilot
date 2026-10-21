// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NXPilot.Integrations;

/// <param name="AssignedTo">Responsável (nome de exibição; no GitHub, os logins); vazio = sem responsável.</param>
public sealed record WorkItem(int Id, string Type, string Title, string State, string Description, string AcceptanceCriteria, string Url, string AssignedTo = "");

/// <summary>Um item da hierarquia (TreeAsync): nível 0 = o item escolhido.</summary>
/// <param name="CompletedWork">Horas trabalhadas (Completed Work).</param>
/// <param name="CurrentHours">HH Atual do NXProject (estimativa atual, campo HH_Atual_float), quando existe.</param>
/// <param name="Created">Data de criação: separa as tasks do plano original das que entraram depois.</param>
public sealed record WorkItemNode(int Id, int ParentId, int Depth, string Type, string Title, string State, string AssignedTo,
    double? RemainingWork, double? OriginalEstimate, string Iteration, string Description, string AcceptanceCriteria, string Url,
    double? CompletedWork = null, double? CurrentHours = null, DateTime? Created = null)
{
    public WorkItem ToWorkItem() => new(Id, Type, Title, State, Description, AcceptanceCriteria, Url, AssignedTo);
}

/// <summary>
/// Work item vinculado à tarefa (origem, projeto, id): vai para o prompt do agente, que lê o item
/// e a hierarquia dele pelo MCP (workitems_tree) — e fica no Histórico.
/// </summary>
public sealed record WorkItemLink(string Source, string Project, int Id, string Type, string Title, string Url)
{
    public string Label => $"{Type} #{Id} · {Title}";
}

/// <summary>Conexão Azure DevOps/TFS: organização, projeto e PAT (decifrado só em memória).</summary>
public sealed record DevOpsConnection(string OrganizationUrl, string Project, string Pat)
{
    /// <summary>
    /// Reaproveita a conexão que o NXProject já tem (config_nxproject.json, PAT em DPAPI do
    /// usuário). Somente LEITURA: o NXPilot nunca grava no arquivo do NXProject.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DevOpsConnection? FromNXProject()
    {
        try
        {
            if (NXProjectConfig.Read() is not { } root) return null;
            var org = NXProjectConfig.Str(root, "OrganizationUrl");
            var project = NXProjectConfig.Str(root, "TeamProject");
            var enc = NXProjectConfig.Str(root, "EncryptedToken");
            if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(enc)) return null;
            var pat = NXProjectConfig.Decrypt(enc);
            return pat.Length == 0 ? null : new DevOpsConnection(org.TrimEnd('/'), project, pat);
        }
        catch { return null; } // outro usuário/máquina ou config inválida
    }
}

/// <summary>
/// Leitura de work items pela REST API do Azure DevOps (WIQL + workitems, api-version 7.1).
/// Só GET/consulta — o NXPilot não altera nada no DevOps nesta versão.
/// </summary>
public sealed class AzureDevOpsClient(DevOpsConnection connection) : IDisposable
{
    private readonly HttpClient _http = CreateClient(connection.Pat);

    private static HttpClient CreateClient(string pat)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private const string OpenStates = "('Closed','Done','Removed','Resolved','Concluído','Fechado','Removido')";

    /// <summary>
    /// Itens atribuídos a mim e ainda abertos (Story, Bug, Task), mais recentes primeiro. Com
    /// <paramref name="within"/>, só os que estão nesse conjunto (os descendentes de um work item Project).
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> MyOpenWorkItemsAsync(int top = 50, ISet<int>? within = null, CancellationToken ct = default, bool includeClosed = false)
    {
        var wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me " +
                   (includeClosed ? "" : $"AND [System.State] NOT IN {OpenStates} ") +
                   "AND [System.WorkItemType] IN ('User Story','Product Backlog Item','Bug','Task','Requirement') ORDER BY [System.ChangedDate] DESC";
        // Com filtro, pede mais ids (só ids, é leve) para sobrar o suficiente depois de filtrar.
        var ids = (await WiqlIdsAsync(wiql, within == null ? top : 1000, ct)).Where(id => within == null || within.Contains(id)).Take(top).ToList();
        if (ids.Count == 0) return Array.Empty<WorkItem>();
        var project = Uri.EscapeDataString(connection.Project);

        const string fields = "System.Id,System.WorkItemType,System.Title,System.State,System.AssignedTo,System.Description,Microsoft.VSTS.Common.AcceptanceCriteria";
        using var itemsResp = await _http.GetAsync($"{connection.OrganizationUrl}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields={fields}&api-version=7.1", ct);
        await EnsureOk(itemsResp, ct);
        using var doc = JsonDocument.Parse(await itemsResp.Content.ReadAsStringAsync(ct));
        var byId = new Dictionary<int, WorkItem>();
        foreach (var w in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var f = w.GetProperty("fields");
            string F(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var id = w.GetProperty("id").GetInt32();
            byId[id] = new WorkItem(id, F("System.WorkItemType"), F("System.Title"), F("System.State"),
                PlainText(F("System.Description")), PlainText(F("Microsoft.VSTS.Common.AcceptanceCriteria")),
                $"{connection.OrganizationUrl}/{project}/_workitems/edit/{id}", AssignedName(f));
        }
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList(); // mantém a ordem do WIQL
    }

    /// <summary>Work items do nível Project ainda abertos no Team Project (o "projeto executado"), por título.</summary>
    public async Task<IReadOnlyList<(int Id, string Title)>> ListProjectWorkItemsAsync(IEnumerable<string> types, CancellationToken ct = default)
    {
        var typeList = string.Join(",", types.Select(t => "'" + t.Replace("'", "''") + "'"));
        if (typeList.Length == 0) return Array.Empty<(int, string)>();
        var ids = await WiqlIdsAsync("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project " +
                                     $"AND [System.WorkItemType] IN ({typeList}) AND [System.State] NOT IN {OpenStates}", 200, ct);
        if (ids.Count == 0) return Array.Empty<(int, string)>();
        using var resp = await _http.GetAsync($"{connection.OrganizationUrl}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields=System.Id,System.Title&api-version=7.1", ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(w => (w.GetProperty("id").GetInt32(), w.GetProperty("fields").TryGetProperty("System.Title", out var t) ? t.GetString() ?? "" : ""))
            .OrderBy(p => p.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Todos os descendentes de um work item na hierarquia (Epic → Feature → Story → Task…).</summary>
    public async Task<HashSet<int>> DescendantIdsAsync(int rootId, CancellationToken ct = default)
    {
        var wiql = $"SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] = {rootId} " +
                   "AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' MODE (Recursive)";
        using var resp = await _http.PostAsync($"{connection.OrganizationUrl}/{Uri.EscapeDataString(connection.Project)}/_apis/wit/wiql?api-version=7.1",
            new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json"), ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ids = new HashSet<int>();
        if (doc.RootElement.TryGetProperty("workItemRelations", out var relations))
            foreach (var r in relations.EnumerateArray())
                if (r.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object) ids.Add(target.GetProperty("id").GetInt32());
        ids.Remove(rootId);
        return ids;
    }

    /// <summary>
    /// O work item e a hierarquia abaixo dele (Epic → Feature → Story → Task…), na ordem da árvore,
    /// com nível, tipo, estado, responsável e horas. <paramref name="openOnly"/> tira os fechados
    /// (o item raiz sempre vem). Até <paramref name="max"/> itens.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemNode>> TreeAsync(int rootId, bool openOnly = false, int max = 300, CancellationToken ct = default)
    {
        var wiql = $"SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] = {rootId} " +
                   "AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' MODE (Recursive)";
        using var resp = await _http.PostAsync($"{connection.OrganizationUrl}/{Uri.EscapeDataString(connection.Project)}/_apis/wit/wiql?api-version=7.1",
            new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json"), ct);
        await EnsureOk(resp, ct);
        using var links = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var children = new Dictionary<int, List<int>>();
        if (links.RootElement.TryGetProperty("workItemRelations", out var relations))
            foreach (var r in relations.EnumerateArray())
                if (r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object && r.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    var parent = s.GetProperty("id").GetInt32();
                    if (!children.TryGetValue(parent, out var list)) children[parent] = list = new();
                    list.Add(t.GetProperty("id").GetInt32());
                }

        // Ordem da árvore (pai antes dos filhos), com o nível de cada um.
        var order = new List<(int Id, int Parent, int Depth)>();
        void Walk(int id, int parent, int depth)
        {
            if (order.Count >= 2000 || order.Any(o => o.Id == id)) return;
            order.Add((id, parent, depth));
            foreach (var c in children.GetValueOrDefault(id) ?? []) Walk(c, id, depth + 1);
        }
        Walk(rootId, 0, 0);

        // Todos os campos: os de horas do NXProject (HH_Atual_float…) são personalizados e pedir um campo
        // que não existe no processo derruba a consulta inteira.
        var byId = new Dictionary<int, JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var chunk in order.Select(o => o.Id).Chunk(200))
            {
                using var r = await _http.GetAsync($"{connection.OrganizationUrl}/_apis/wit/workitems?ids={string.Join(',', chunk)}&errorPolicy=omit&api-version=7.1", ct);
                await EnsureOk(r, ct);
                var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                docs.Add(doc);
                foreach (var w in doc.RootElement.GetProperty("value").EnumerateArray())
                    if (w.ValueKind == JsonValueKind.Object) byId[w.GetProperty("id").GetInt32()] = w.GetProperty("fields");
            }
            var project = Uri.EscapeDataString(connection.Project);
            string StateOf(int id) => byId.TryGetValue(id, out var f) && f.TryGetProperty("System.State", out var v) ? v.GetString() ?? "" : "";

            // Só pendentes: fica o que está aberto e os pais dele (mesmo fechados), para a Task não aparecer solta.
            HashSet<int>? keep = null;
            if (openOnly)
            {
                var parentOf = order.ToDictionary(o => o.Id, o => o.Parent);
                keep = new HashSet<int> { rootId };
                foreach (var (id, _, _) in order.Where(o => byId.ContainsKey(o.Id) && !IsClosed(StateOf(o.Id))))
                    for (var cur = id; cur != 0 && keep.Add(cur); cur = parentOf.GetValueOrDefault(cur)) { }
            }

            var result = new List<WorkItemNode>();
            foreach (var (id, parent, depth) in order)
            {
                if (!byId.TryGetValue(id, out var f)) continue;
                string F(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                double? N(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                // Campo personalizado pelo fim do nome (Custom.HH_Atual_Float, Custom.HH_Original_float…), sem diferenciar maiúsculas.
                double? Custom(string suffix) => f.EnumerateObject().Where(p => p.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                    .Select(p => (double?)p.Value.GetDouble()).FirstOrDefault();
                DateTime? created = f.TryGetProperty("System.CreatedDate", out var cd) && cd.ValueKind == JsonValueKind.String && cd.TryGetDateTime(out var cdt) ? cdt : null;
                var state = F("System.State");
                if (keep != null && !keep.Contains(id)) continue;
                var assigned = f.TryGetProperty("System.AssignedTo", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                result.Add(new WorkItemNode(id, parent, depth, F("System.WorkItemType"), F("System.Title"), state, assigned,
                    N("Microsoft.VSTS.Scheduling.RemainingWork") ?? Custom("HH_Restante_float"),
                    N("Microsoft.VSTS.Scheduling.OriginalEstimate") ?? Custom("HH_Original_float"), F("System.IterationPath"),
                    PlainText(F("System.Description")), PlainText(F("Microsoft.VSTS.Common.AcceptanceCriteria")),
                    $"{connection.OrganizationUrl}/{project}/_workitems/edit/{id}",
                    N("Microsoft.VSTS.Scheduling.CompletedWork"), Custom("HH_Atual_float"), created));
                if (result.Count >= max) break;
            }
            return result;
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }

    private static string AssignedName(JsonElement fields) =>
        fields.TryGetProperty("System.AssignedTo", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";

    /// <summary>Em execução (não só aberta): Active, In Progress, Committed, Doing…</summary>
    public static bool IsInProgress(string state) =>
        state is "Active" or "In Progress" or "Committed" or "Doing" or "Ativo" or "Em andamento" or "Em execução" or "Em Andamento";

    public static bool IsClosed(string state) =>
        state is "Closed" or "Done" or "Removed" or "Resolved" or "Concluído" or "Fechado" or "Removido";

    private async Task<List<int>> WiqlIdsAsync(string wiql, int top, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{connection.OrganizationUrl}/{Uri.EscapeDataString(connection.Project)}/_apis/wit/wiql?$top={top}&api-version=7.1",
            new StringContent(JsonSerializer.Serialize(new { query = wiql }), Encoding.UTF8, "application/json"), ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).Take(top).ToList();
    }

    /// <summary>Projetos da organização que o PAT enxerga, em ordem alfabética.</summary>
    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{connection.OrganizationUrl}/_apis/projects?$top=500&api-version=7.1", ct);
        await EnsureOk(resp, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task EnsureOk(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation
            ? NxText.T("O Azure DevOps recusou o token (expirado ou sem permissão de Work Items). Atualize o PAT na extensão Work items ou no NXProject.", "Azure DevOps rejected the token (expired or without Work Items permission). Update the PAT in the Work items extension or in NXProject.", "Azure DevOps rechazó el token (vencido o sin permiso de Work Items). Actualice el PAT en la extensión Work items o en NXProject.")
            : $"Azure DevOps {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
    }

    /// <summary>Descrição/critérios vêm em HTML: vira texto simples para o prompt do agente.</summary>
    public static string PlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = Regex.Replace(html, @"<(br|/p|/div|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    public void Dispose() => _http.Dispose();
}
