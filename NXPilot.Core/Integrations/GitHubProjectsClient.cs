// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NXPilot.Integrations;

/// <summary>
/// Leitura dos itens de um projeto do GitHub Projects (v2) pela API GraphQL — só consulta, o
/// NXPilot não altera nada no GitHub. O token precisa do escopo read:project (e repo para issues
/// de repositório privado), o mesmo que o NXProject usa.
/// </summary>
public sealed class GitHubProjectsClient(GitHubProjectConnection connection, HttpMessageHandler? handler = null,
    string graphQlUrl = "https://api.github.com/graphql", string levelField = "cardType") : IDisposable
{
    private readonly HttpClient _http = CreateClient(connection.Token, handler);

    private static HttpClient CreateClient(string token, HttpMessageHandler? handler)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NXPilot");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    // Nível (como no NXProject): tipo da issue, campo de nível do board (card_type) ou posição na
    // árvore de sub-issues (parent). O nome do campo de nível vem do NXProject.
    private const string Query = """
        query($owner: String!, $number: Int!, $after: String, $level: String!) {
          viewer { login }
          owner: OWNER(login: $owner) {
            projectV2(number: $number) {
              title
              items(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  status: fieldValueByName(name: "Status") { ... on ProjectV2ItemFieldSingleSelectValue { name } }
                  level: fieldValueByName(name: $level) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                    ... on ProjectV2ItemFieldTextValue { text }
                  }
                  content {
                    __typename
                    ... on Issue { number title body state url issueType { name } parent { number } assignees(first: 20) { nodes { login } } }
                    ... on DraftIssue { title body assignees(first: 20) { nodes { login } } }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ProjectsQuery = """
        query($owner: String!) {
          owner: OWNER(login: $owner) { projectsV2(first: 50, orderBy: {field: NUMBER, direction: DESC}) { nodes { number title closed } } }
        }
        """;

    /// <summary>Projetos abertos do dono (usuário ou organização): número e título.</summary>
    public async Task<IReadOnlyList<(int Number, string Title)>> ListProjectsAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync(ProjectsQuery, new { owner = connection.Owner }, ct);
        var owner = doc.RootElement.GetProperty("data").GetProperty("owner");
        if (owner.ValueKind == JsonValueKind.Null) return Array.Empty<(int, string)>();
        return owner.GetProperty("projectsV2").GetProperty("nodes").EnumerateArray()
            .Where(p => !p.GetProperty("closed").GetBoolean())
            .Select(p => (p.GetProperty("number").GetInt32(), p.GetProperty("title").GetString() ?? "")).ToList();
    }

    /// <summary>POST no GraphQL com as checagens de token, HTTP e "errors" (que vem com HTTP 200).</summary>
    private async Task<JsonDocument> PostAsync(string query, object variables, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query = query.Replace("OWNER", connection.OwnerIsOrganization ? "organization" : "user"), variables });
        using var resp = await _http.PostAsync(graphQlUrl, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(NxText.T(
                "O GitHub recusou o token (expirado ou sem o escopo read:project). Atualize o token na extensão Work items ou no NXProject.",
                "GitHub rejected the token (expired or without the read:project scope). Update the token in the Work items extension or in NXProject.",
                "GitHub rechazó el token (vencido o sin el alcance read:project). Actualice el token en la extensión Work items o en NXProject."));
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub {(int)resp.StatusCode}: {Short(text)}");
        var doc = JsonDocument.Parse(text);
        // No GraphQL o erro vem com HTTP 200: sem olhar "errors", projeto errado viraria lista vazia.
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var message = "GitHub: " + string.Join("; ", errors.EnumerateArray().Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : "?"));
            doc.Dispose();
            throw new InvalidOperationException(message);
        }
        return doc;
    }

    /// <summary>Título do projeto (preenchido depois da primeira consulta).</summary>
    public string? ProjectTitle { get; private set; }

    /// <summary>Item do board (issue ou rascunho), com o que é preciso para nível e hierarquia.</summary>
    private sealed record BoardItem(bool IsIssue, int Number, string Title, bool Open, IReadOnlyList<string> Assignees,
        string? Status, string Body, string Url, int Parent, string? IssueType, string? Level);

    private (string Me, List<BoardItem> Items)? _board;

    /// <summary>Lê o board inteiro (até <paramref name="maxPages"/> páginas de 100), uma vez por cliente.</summary>
    private async Task<(string Me, List<BoardItem> Items)> LoadBoardAsync(int maxPages, CancellationToken ct)
    {
        if (_board is { } cached) return cached;
        var items = new List<BoardItem>();
        var me = "";
        string? after = null;
        for (var page = 0; page < maxPages; page++)
        {
            using var doc = await PostAsync(Query, new { owner = connection.Owner, number = connection.Number, after, level = levelField }, ct);
            var data = doc.RootElement.GetProperty("data");
            me = data.GetProperty("viewer").GetProperty("login").GetString() ?? "";
            if (data.GetProperty("owner").ValueKind == JsonValueKind.Null || data.GetProperty("owner").GetProperty("projectV2").ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException(NxText.T($"Projeto {connection.Number} não encontrado em {connection.Owner}.", $"Project {connection.Number} not found under {connection.Owner}.", $"Proyecto {connection.Number} no encontrado en {connection.Owner}."));
            var project = data.GetProperty("owner").GetProperty("projectV2");
            ProjectTitle = project.GetProperty("title").GetString();
            var nodes = project.GetProperty("items");

            foreach (var node in nodes.GetProperty("nodes").EnumerateArray())
            {
                if (node.GetProperty("content") is not { ValueKind: JsonValueKind.Object } content) continue;
                var kind = content.GetProperty("__typename").GetString();
                if (kind is not ("Issue" or "DraftIssue")) continue; // pull requests ficam de fora
                var issue = kind == "Issue";
                items.Add(new BoardItem(issue,
                    issue ? content.GetProperty("number").GetInt32() : 0,
                    content.GetProperty("title").GetString() ?? "",
                    !issue || content.GetProperty("state").GetString() == "OPEN",
                    content.GetProperty("assignees").GetProperty("nodes").EnumerateArray().Select(a => a.GetProperty("login").GetString() ?? "").ToList(),
                    Child(node, "status", "name"),
                    content.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    issue && content.TryGetProperty("url", out var u) ? u.GetString() ?? connection.WebUrl : connection.WebUrl,
                    issue && content.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetProperty("number").GetInt32() : 0,
                    Child(content, "issueType", "name"),
                    Child(node, "level", "name") ?? Child(node, "level", "text")));
            }

            var pageInfo = nodes.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) break;
            after = pageInfo.GetProperty("endCursor").GetString();
        }
        _board = (me, items);
        return _board.Value;
    }

    private static string? Child(JsonElement parent, string name, string field) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty(field, out var f) ? f.GetString() : null;

    /// <summary>
    /// Issues do nível Project no board (o "work item Project" do NXProject): pelo tipo da issue,
    /// pelo campo de nível ou, sem os dois, por estar no topo da árvore com sub-issues penduradas.
    /// </summary>
    public async Task<IReadOnlyList<(int Number, string Title)>> ListRootIssuesAsync(IEnumerable<string> projectLevelNames, int maxPages = 5, CancellationToken ct = default)
    {
        var names = new HashSet<string>(projectLevelNames, StringComparer.OrdinalIgnoreCase);
        var (_, items) = await LoadBoardAsync(maxPages, ct);
        var hasChildren = items.Where(i => i.Parent > 0).Select(i => i.Parent).ToHashSet();
        return items.Where(i => i.IsIssue && i.Open &&
                (i.IssueType is { } t && names.Contains(t) || i.Level is { } l && names.Contains(l)
                 || i.IssueType == null && i.Level == null && i.Parent == 0 && hasChildren.Contains(i.Number)))
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => (i.Number, i.Title)).ToList();
    }

    /// <summary>
    /// A issue e as sub-issues abaixo dela no board, na ordem da árvore (nível 0 = a escolhida),
    /// com tipo (tipo da issue ou nível), estado, status do board e responsáveis — o par do
    /// TreeAsync do DevOps. <paramref name="openOnly"/> tira as fechadas, mas mantém os pais delas.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemNode>> TreeAsync(int rootNumber, bool openOnly = false, int max = 300, int maxPages = 5, CancellationToken ct = default)
    {
        var (_, items) = await LoadBoardAsync(maxPages, ct);
        var byNumber = items.Where(i => i.IsIssue).GroupBy(i => i.Number).ToDictionary(g => g.Key, g => g.First());
        if (!byNumber.ContainsKey(rootNumber)) return Array.Empty<WorkItemNode>();
        var children = items.Where(i => i.IsIssue && i.Parent > 0).GroupBy(i => i.Parent).ToDictionary(g => g.Key, g => g.Select(i => i.Number).ToList());

        var order = new List<(int Number, int Parent, int Depth)>();
        void Walk(int n, int parent, int depth)
        {
            if (order.Count >= 2000 || order.Any(o => o.Number == n)) return;
            order.Add((n, parent, depth));
            foreach (var c in children.GetValueOrDefault(n) ?? []) Walk(c, n, depth + 1);
        }
        Walk(rootNumber, 0, 0);

        HashSet<int>? keep = null;
        if (openOnly)
        {
            var parentOf = order.ToDictionary(o => o.Number, o => o.Parent);
            keep = new HashSet<int> { rootNumber };
            foreach (var o in order.Where(o => byNumber[o.Number].Open))
                for (var cur = o.Number; cur != 0 && keep.Add(cur); cur = parentOf.GetValueOrDefault(cur)) { }
        }

        var result = new List<WorkItemNode>();
        foreach (var (n, parent, depth) in order)
        {
            if (keep != null && !keep.Contains(n)) continue;
            var i = byNumber[n];
            var (description, criteria) = SplitCriteria(i.Body);
            var state = i.Open ? i.Status ?? NxText.T("Aberta", "Open", "Abierta") : NxText.T("Fechada", "Closed", "Cerrada") + (i.Status is { } s ? $" ({s})" : "");
            result.Add(new WorkItemNode(n, parent, depth, i.IssueType ?? i.Level ?? "Issue", i.Title, state, string.Join(", ", i.Assignees),
                null, null, "", description, criteria, i.Url));
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>
    /// Itens abertos atribuídos a mim (issues abertas e rascunhos), na ordem do board. Com
    /// <paramref name="rootIssue"/>, só os que descendem dela na árvore de sub-issues.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> MyOpenItemsAsync(int rootIssue = 0, int maxPages = 5, CancellationToken ct = default)
    {
        var (me, items) = await LoadBoardAsync(maxPages, ct);
        var byNumber = items.Where(i => i.IsIssue).GroupBy(i => i.Number).ToDictionary(g => g.Key, g => g.First());
        bool Under(BoardItem item)
        {
            var current = item;
            for (var depth = 0; depth < 10 && current.Parent > 0; depth++)
            {
                if (current.Parent == rootIssue) return true;
                if (!byNumber.TryGetValue(current.Parent, out var parent)) return false; // pai fora do board
                current = parent;
            }
            return false;
        }
        return items
            .Where(i => i.Open && i.Assignees.Any(a => string.Equals(a, me, StringComparison.OrdinalIgnoreCase)))
            .Where(i => rootIssue <= 0 || Under(i))
            .Select(i =>
            {
                var (description, criteria) = SplitCriteria(i.Body);
                return new WorkItem(i.Number, i.IsIssue ? i.IssueType ?? i.Level ?? "Issue" : NxText.T("Rascunho", "Draft", "Borrador"), i.Title,
                    i.Status ?? (i.IsIssue ? NxText.T("Aberta", "Open", "Abierta") : ""), description, criteria, i.Url, string.Join(", ", i.Assignees));
            }).ToList();
    }

    private static readonly Regex CriteriaHeading = new(
        @"^(#{1,6}\s*|\*\*)?\s*(crit[ée]rios?\s+de\s+aceit\w*|acceptance\s+criteria|criterios?\s+de\s+aceptaci[óo]n)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Issue não tem campo de critérios de aceite: se o corpo tiver uma seção com esse título
    /// ("## Critérios de aceite", "Acceptance criteria"…), ela vira os critérios e sai da descrição.
    /// </summary>
    public static (string Description, string Criteria) SplitCriteria(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Trim();
        var m = CriteriaHeading.Match(text);
        if (!m.Success) return (text, "");
        var rest = text[(m.Index + m.Length)..];
        var next = Regex.Match(rest, @"^#{1,6}\s", RegexOptions.Multiline); // a seção vai até o próximo título
        var criteria = (next.Success ? rest[..next.Index] : rest).Trim();
        var description = (text[..m.Index] + (next.Success ? rest[next.Index..] : "")).Trim();
        return (description, criteria);
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
