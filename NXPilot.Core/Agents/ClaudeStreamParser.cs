// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text;
using System.Text.Json;

namespace NXPilot.Agents;

/// <summary>
/// Converte cada linha do `claude -p --output-format stream-json --verbose` em AgentEvent.
/// Leitura tolerante (JsonDocument), como no webhook do WhatsApp: tipo novo é ignorado,
/// campo ausente vira nulo. O formato fica travado pelos exemplos do smoke test.
/// </summary>
public static class ClaudeStreamParser
{
    public static IEnumerable<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { yield break; } // linha cortada ou não-JSON: ignora
        using (doc)
        {
            var root = doc.RootElement;
            var type = Str(root, "type");
            var session = Str(root, "session_id");

            switch (type)
            {
                case "system" when Str(root, "subtype") == "init":
                    yield return new AgentEvent(AgentEventKind.Started,
                        $"{Str(root, "model")} · {Str(root, "cwd")}", SessionId: session, Raw: line);
                    break;

                case "assistant":
                    foreach (var block in Blocks(root))
                    {
                        switch (Str(block, "type"))
                        {
                            case "text" when Str(block, "text") is { Length: > 0 } t:
                                yield return new AgentEvent(AgentEventKind.Text, t, SessionId: session);
                                break;
                            case "tool_use":
                                var name = Str(block, "name") ?? "?";
                                var input = block.TryGetProperty("input", out var i) ? i : default;
                                yield return new AgentEvent(AgentEventKind.ToolUse, DescribeTool(name, input), name, session);
                                break;
                        }
                    }
                    break;

                case "user":
                    foreach (var block in Blocks(root))
                        if (Str(block, "type") == "tool_result")
                        {
                            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                            yield return new AgentEvent(AgentEventKind.ToolResult, Short(ResultText(block)), SessionId: session, IsError: isError);
                        }
                    break;

                case "result":
                    var cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var d) ? d : (decimal?)null;
                    var failed = Str(root, "subtype") != "success" || (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True);
                    yield return new AgentEvent(failed ? AgentEventKind.Error : AgentEventKind.Result,
                        Str(root, "result") ?? Str(root, "subtype") ?? "", SessionId: session, IsError: failed, CostUsd: cost, Raw: line,
                        Tokens: Usage(root));
                    break;
            }
        }
    }

    /// <summary>
    /// Ferramenta → texto no MESMO formato dos exemplos do classificador ("Bash: …", "Edit: …"),
    /// para o DecisionGate classificar a ação real que o agente quer executar.
    /// </summary>
    public static string DescribeTool(string name, JsonElement input)
    {
        string? Field(params string[] keys)
        {
            if (input.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys) if (Str(input, k) is { } v) return v;
            return null;
        }
        var detail = name switch
        {
            "Bash" or "PowerShell" => Field("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => Field("file_path", "notebook_path"),
            "Glob" or "Grep" => Field("pattern"),
            "WebFetch" => Field("url"),
            "WebSearch" => Field("query"),
            "Task" or "Agent" => Field("description", "prompt"),
            _ => input.ValueKind == JsonValueKind.Object ? input.GetRawText() : null
        };
        return $"{name}: {Short(detail ?? "", 400)}";
    }

    private static IEnumerable<JsonElement> Blocks(JsonElement root) =>
        root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        var sb = new StringBuilder();
        if (c.ValueKind == JsonValueKind.Array)
            foreach (var part in c.EnumerateArray())
                if (Str(part, "text") is { } t) sb.Append(t);
        return sb.ToString();
    }

    private static string Short(string s, int max = 240)
    {
        s = s.Replace("\r", "").Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>"usage" do evento result: tokens desta execução (entrada, saída, cache lido/gravado).</summary>
    private static TokenUsage? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;
        long N(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var usage = new TokenUsage(N("input_tokens"), N("output_tokens"), N("cache_read_input_tokens"), N("cache_creation_input_tokens"));
        return usage.Total > 0 ? usage : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
