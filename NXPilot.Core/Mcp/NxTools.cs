// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;
using System.Text.Json.Nodes;
using NXPilot.Integrations;
using NXPilot.Risk;

namespace NXPilot.Mcp;

/// <summary>Conteúdo de resposta de uma ferramenta: texto ou imagem (base64), como o MCP define.</summary>
public sealed record ToolContent(string Type, string? Text = null, string? Data = null, string? MimeType = null)
{
    public static ToolContent OfText(string text) => new("text", text);
    public static ToolContent OfImage(byte[] bytes, string mimeType = "image/png") => new("image", Data: Convert.ToBase64String(bytes), MimeType: mimeType);

    public JsonObject ToJson() => Type == "image"
        ? new JsonObject { ["type"] = "image", ["data"] = Data, ["mimeType"] = MimeType }
        : new JsonObject { ["type"] = "text", ["text"] = Text };
}

public sealed record ToolResult(IReadOnlyList<ToolContent> Content, bool IsError = false)
{
    public static ToolResult Text(string text) => new([ToolContent.OfText(text)]);
    public static ToolResult Error(string text) => new([ToolContent.OfText(text)], true);

    public JsonObject ToJson() => new()
    {
        ["content"] = new JsonArray(Content.Select(c => (JsonNode)c.ToJson()).ToArray()),
        ["isError"] = IsError,
    };
}

/// <summary>
/// O que a ferramenta sabe da tarefa: o worktree (a "caixa" do agente), se a fase só lê
/// (plano/revisão) e quem aprova — o mesmo painel de decisões do gate.
/// </summary>
public sealed class ToolContext
{
    public required string Worktree { get; init; }
    /// <summary>Plano ou revisão: ferramenta que altera algo é recusada.</summary>
    public bool ReadOnlyPhase { get; init; }
    /// <summary>Ação + risco → liberado? (auto-aprova BAIXO, regras da tarefa, ou pergunta a você).</summary>
    public required Func<string, RiskResult, CancellationToken, Task<bool>> Approve { get; init; }
    public PowerBiProject? PowerBi { get; init; }

    /// <summary>Power BI Desktop instalado: as ferramentas Power BI aparecem mesmo antes de existir um .pbip (o agente pode criá-lo).</summary>
    public bool PowerBiInstalled { get; init; }

    /// <summary>O projeto PBIP do worktree agora — procura de novo quando ele foi criado depois do início da tarefa.</summary>
    public PowerBiProject? PowerBiNow => PowerBi ?? PowerBiProject.Detect(Worktree);

    /// <summary>Ferramentas Power BI disponíveis: há um .pbip ou o Power BI Desktop está instalado.</summary>
    public bool PowerBiToolsAvailable => PowerBi != null || PowerBiInstalled;
    public Action<string>? Log { get; init; }

    /// <summary>Registra uma escolha feita pela pessoa numa janela do NXPilot (ex.: as figuras) — vai ao log e é evidência para o revisor.</summary>
    public Action<string>? UserChoice { get; init; }

    /// <summary>Caminho relativo ao worktree (ou absoluto), normalizado.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Worktree, path));

    public bool IsInside(string fullPath)
    {
        var root = Path.GetFullPath(Worktree).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (Path.GetFullPath(fullPath) + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Arquivos e pastas que o usuário citou na tarefa ([xlsx01], [pbip01], [folder01]…): ler é consentido.</summary>
    public IReadOnlyList<string> CitedPaths { get; init; } = [];

    /// <summary>
    /// O caminho é um arquivo citado, está dentro de uma pasta citada ou, para um .pbip citado, nas
    /// pastas .Report/.SemanticModel dele (o projeto Power BI é o .pbip + essas pastas).
    /// </summary>
    public bool IsCited(string fullPath)
    {
        var full = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var cited in CitedPaths)
        {
            var c = Path.GetFullPath(cited).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, c, StringComparison.OrdinalIgnoreCase)) return true;
            var roots = new List<string>();
            if (Directory.Exists(c)) roots.Add(c);
            if (c.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.Combine(Path.GetDirectoryName(c)!, Path.GetFileNameWithoutExtension(c));
                roots.Add(stem + ".Report");
                roots.Add(stem + ".SemanticModel");
            }
            if (roots.Any(r => (full + Path.DirectorySeparatorChar).StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>
    /// Mesma regra do gate para arquivos: dentro do worktree é BAIXO; ler o que o usuário citou é BAIXO;
    /// ler fora é MÉDIO; gravar fora é ALTO (mesmo se citado — o exemplo nunca é alterado).
    /// </summary>
    public RiskResult PathRisk(string? path, bool write)
    {
        if (string.IsNullOrWhiteSpace(path)) return NxTool.Low();
        var full = Resolve(path);
        if (IsInside(full)) return NxTool.Low(write ? "grava no worktree" : "lê do worktree");
        if (!write && IsCited(full)) return NxTool.Low(NxText.T("lê arquivo citado na tarefa", "reads a file cited in the task", "lee un archivo citado en la tarea"));
        return write
            ? new RiskResult(RiskLevel.Alto, 1, "MCP", NxText.T("grava FORA do worktree da tarefa", "writes OUTSIDE the task worktree", "escribe FUERA del worktree de la tarea"), TimeSpan.Zero)
            : new RiskResult(RiskLevel.Medio, 0.9, "MCP", NxText.T("lê FORA do worktree da tarefa", "reads OUTSIDE the task worktree", "lee FUERA del worktree de la tarea"), TimeSpan.Zero);
    }
}

/// <summary>Uma ferramenta do MCP NXPilot: nome, extensão de origem, esquema dos argumentos, risco e execução.</summary>
public sealed class NxTool
{
    public required string Name { get; init; }
    /// <summary>Extensão de origem (Power BI, PowerPoint, Databricks…), para a tela Configurar.</summary>
    public required string Extension { get; init; }
    public required string Description { get; init; }
    public required JsonObject Schema { get; init; }
    /// <summary>Só lê/consulta: pode ser usada no plano e na revisão.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Aparece para o agente? (extensão configurada, PBIP no worktree…). Nulo = sempre.</summary>
    public Func<ToolContext, bool>? Available { get; init; }
    /// <summary>Risco da chamada; nulo = BAIXO (ou o gate fica DENTRO da extensão, como no SQL e no git).</summary>
    public Func<JsonObject, ToolContext, RiskResult>? Risk { get; init; }
    public required Func<JsonObject, ToolContext, CancellationToken, Task<ToolResult>> Run { get; init; }

    public static RiskResult Low(string? reason = null) => new(RiskLevel.Baixo, 0.95, "MCP", reason, TimeSpan.Zero);
    public static RiskResult Medium(string reason) => new(RiskLevel.Medio, 0.9, "MCP", reason, TimeSpan.Zero);

    public static JsonObject ParseSchema(string json) => (JsonObject)JsonNode.Parse(json)!;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = $"{Extension} · {Name}",
        ["description"] = Description,
        ["inputSchema"] = Schema.DeepClone(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = ReadOnly },
    };
}

/// <summary>
/// Catálogo do MCP NXPilot no app: lista o que está disponível na tarefa e executa uma chamada
/// passando pelo gate. Ordem de uma chamada: existe e está disponível → fase só-leitura recusa
/// quem altera → risco → aprovação (painel) → execução. Negado = NADA executa.
/// </summary>
public sealed class NxToolHost(IEnumerable<NxTool> tools)
{
    public IReadOnlyList<NxTool> Tools { get; } = tools.ToList();

    public IReadOnlyList<NxTool> AvailableTools(ToolContext ctx) => Tools.Where(t =>
    {
        try { return t.Available?.Invoke(ctx) ?? true; }
        catch { return false; } // configuração ilegível: a ferramenta simplesmente não aparece
    }).ToList();

    /// <summary>Pedido que chega pelo pipe (method + params) → resposta (result ou error).</summary>
    public async Task<JsonObject> HandleAsync(JsonObject request, ToolContext? ctx, CancellationToken ct)
    {
        if (ctx == null) return new JsonObject { ["error"] = NxText.T("Nenhuma tarefa em execução no NXPilot.", "No task running in NXPilot.", "Ninguna tarea en ejecución en NXPilot.") };
        var p = request["params"] as JsonObject ?? new JsonObject();
        switch (request["method"]?.GetValue<string>())
        {
            case "tools/list":
                return new JsonObject { ["result"] = new JsonObject { ["tools"] = new JsonArray(AvailableTools(ctx).Select(t => (JsonNode)t.ToJson()).ToArray()) } };
            case "tools/call":
                var name = p["name"]?.GetValue<string>() ?? "";
                var args = p["arguments"] as JsonObject ?? new JsonObject();
                return new JsonObject { ["result"] = (await CallAsync(name, args, ctx, ct)).ToJson() };
            default:
                return new JsonObject { ["error"] = "método desconhecido" };
        }
    }

    public async Task<ToolResult> CallAsync(string name, JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var tool = AvailableTools(ctx).FirstOrDefault(t => t.Name == name);
        if (tool == null)
            return ToolResult.Error(NxText.T($"Ferramenta {name} não está disponível nesta tarefa.", $"Tool {name} is not available in this task.", $"La herramienta {name} no está disponible en esta tarea."));
        if (ctx.ReadOnlyPhase && !tool.ReadOnly)
            return ToolResult.Error(NxText.T($"{name} altera algo e esta fase (plano/revisão) só lê. Inclua no plano; ela roda na execução.",
                $"{name} changes something and this phase (plan/review) is read-only. Put it in the plan; it runs during execution.",
                $"{name} modifica algo y esta fase (plan/revisión) es de solo lectura. Inclúyala en el plan; se ejecuta en la ejecución."));

        RiskResult risk;
        try { risk = tool.Risk?.Invoke(args, ctx) ?? NxTool.Low(); }
        catch (Exception ex) { return ToolResult.Error(ex.Message); } // argumento inválido: nem chega a pedir aprovação

        if (!await ctx.Approve(Describe(name, args), risk, ct))
            return ToolResult.Error(NxText.T("NEGADO pelo usuário no NXPilot — não repita esta chamada; ajuste o plano ou pergunte.",
                "DENIED by the user in NXPilot — do not retry this call; adjust the plan or ask.",
                "DENEGADO por el usuario en NXPilot — no repita esta llamada; ajuste el plan o pregunte."));

        try { return await tool.Run(args, ctx, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error($"{name}: {ex.Message}"); }
    }

    /// <summary>Texto da ação para o painel e o log: "MCP nxpilot pptx_build: {output: …}" (curto).</summary>
    public static string Describe(string name, JsonObject args)
    {
        var shown = new JsonObject();
        foreach (var (k, v) in args)
            shown[k] = v switch
            {
                JsonArray a => $"[{a.Count} item(s)]",
                JsonObject => "{…}",
                _ => v?.DeepClone(),
            };
        var text = shown.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"MCP nxpilot {name}: {(text.Length > 300 ? text[..300] + "…" : text)}";
    }
}

/// <summary>Leitura tolerante dos argumentos (o agente às vezes manda número como texto).</summary>
public static class ToolArgs
{
    public static string? Str(JsonObject a, string key) => a[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var other => other.ToString(),
    };

    public static string Req(JsonObject a, string key) =>
        Str(a, key) is { Length: > 0 } s ? s : throw new ArgumentException(NxText.T($"Falta o argumento \"{key}\".", $"Missing argument \"{key}\".", $"Falta el argumento \"{key}\"."));

    public static int? Int(JsonObject a, string key) => a[key] switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when int.TryParse(v.ToString(), out var p) => p,
        _ => null,
    };

    public static bool Bool(JsonObject a, string key) => a[key] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.ToString() is "true" or "1");

    public static JsonArray Arr(JsonObject a, string key) => a[key] switch
    {
        JsonArray arr => arr,
        // Alguns agentes mandam o array serializado como texto.
        JsonValue v when v.TryGetValue<string>(out var s) && JsonNode.Parse(s) is JsonArray parsed => parsed,
        _ => throw new ArgumentException(NxText.T($"\"{key}\" precisa ser uma lista.", $"\"{key}\" must be a list.", $"\"{key}\" debe ser una lista.")),
    };

    public static List<int> Ints(JsonObject a, string key) => Arr(a, key).Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : int.Parse(n!.ToString())).ToList();
}
