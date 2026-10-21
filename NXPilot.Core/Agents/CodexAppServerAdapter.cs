// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace NXPilot.Agents;

/// <summary>
/// Codex como EXECUTOR, pelo `codex app-server` (JSON-RPC por stdio, uma mensagem por linha).
///
/// O portão: thread com sandbox workspace-write e approvalPolicy "untrusted" — o Codex pede
/// aprovação ao cliente antes de cada comando (item/commandExecution/requestApproval) e de cada
/// alteração de arquivo (item/fileChange/requestApproval). O NXPilot responde pelo MESMO
/// DecisionGate do Claude (<paramref name="gate"/>); recusar ou falhar = "decline".
/// No modo Plan, toda alteração de arquivo é recusada aqui mesmo: planejar só lê.
///
/// Um processo e uma thread do Codex por tarefa (plano e fases continuam a mesma conversa);
/// descarte o adaptador ao fim da tarefa.
/// </summary>
public sealed class CodexAppServerAdapter(Func<GateRequest, Task<GateResponse>> gate, string? model = null, string? executablePath = null)
    : IAgentAdapter, IDisposable
{
    public string Name => "Codex";
    public string? ExecutablePath { get; } = executablePath ?? CodexAdapter.Locate();

    /// <summary>
    /// Modelos já testados de ponta a ponta com o NXPilot (executor e revisor). Atualize
    /// quando validar outro — a tela marca estes como "testado".
    /// </summary>
    public static readonly string[] TestedModels = { "gpt-5.6-terra" };

    public sealed record CodexModel(string Id, string DisplayName, bool IsDefault)
    {
        public bool Tested => TestedModels.Contains(Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Modelos que a conta do usuário aceita, perguntando ao próprio Codex (model/list do
    /// app-server). Resolve o erro "model is not supported when using Codex with a ChatGPT
    /// account": o config.toml pode apontar para um modelo que a conta não tem.
    /// Lista vazia = não foi possível consultar (sem Codex, sem login, timeout).
    /// </summary>
    public static async Task<IReadOnlyList<CodexModel>> ListModelsAsync(string? executablePath = null, CancellationToken ct = default)
    {
        var exe = executablePath ?? CodexAdapter.Locate();
        if (exe == null) return Array.Empty<CodexModel>();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("app-server");
        using var p = Process.Start(psi);
        if (p == null) return Array.Empty<CodexModel>();
        _ = Task.Run(async () => { while (await p.StandardError.ReadLineAsync() != null) { } });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            async Task<JsonNode?> Call(int id, string method, JsonObject prms)
            {
                await p.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = prms }.ToJsonString());
                await p.StandardInput.FlushAsync();
                while (await p.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    if (JsonNode.Parse(line) is JsonObject o && o["id"]?.GetValue<int>() == id)
                        return o["result"] ?? throw new InvalidOperationException(o["error"]?["message"]?.ToString());
                return null;
            }
            await Call(1, "initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "nxpilot", ["version"] = "0.1" } });
            await p.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" }.ToJsonString());
            var result = await Call(2, "model/list", new JsonObject());
            return (result?["data"] as JsonArray ?? new JsonArray())
                .Select(m => new CodexModel(m?["model"]?.ToString() ?? m?["id"]?.ToString() ?? "?",
                                            m?["displayName"]?.ToString() ?? "", m?["isDefault"]?.GetValue<bool>() == true))
                .Where(m => m.Id != "?")
                .ToList();
        }
        catch { return Array.Empty<CodexModel>(); }
        finally { try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ } }
    }

    /// <summary>MCP NXPilot (nulo = parado em Configurar). Vale ao subir o app-server.</summary>
    public McpLaunch? Mcp { get; set; }

    private Process? _process;
    private string? _threadId;

    /// <summary>Tokens acumulados da thread (o Codex informa o total) e o total no início do turno atual.</summary>
    private TokenUsage _threadTokens = new(0, 0), _turnStartTokens = new(0, 0);
    private string? _cwd;
    private int _nextId;
    private AgentMode _mode;
    private Channel<AgentEvent>? _events;
    private string? _lastMessage;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, JsonNode> _items = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ExecutablePath == null)
        {
            yield return new AgentEvent(AgentEventKind.Error, NxText.T("Codex não encontrado no Windows.", "Codex not found on Windows.", "Codex no se encontró en Windows."), IsError: true);
            yield break;
        }

        if (_process is not { HasExited: false }) // app-server já de pé = já logado
        {
            var blocked = false;
            await foreach (var ev in AgentLogin.EnsureAsync("Codex", ExecutablePath, ct)) { blocked |= ev.IsError; yield return ev; }
            if (blocked) yield break;
        }

        _mode = request.Mode;
        _events = Channel.CreateUnbounded<AgentEvent>();
        _lastMessage = null;
        string? startError = null;
        try
        {
            await EnsureThreadAsync(request, ct);
            _turnStartTokens = _threadTokens;
            await RequestAsync("turn/start", new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Prompt }),
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { startError = ex.Message; }

        if (startError != null)
        {
            var detail = startError + " " + _stderrTail;
            yield return new AgentEvent(AgentEventKind.Error, "Codex: " + startError + StderrNote() + OutdatedAgent.HintIfOutdated("Codex", ExecutablePath, detail), IsError: true);
            yield break;
        }

        using var stop = ct.Register(() => _ = InterruptAsync());
        await foreach (var ev in _events.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return ev;
            if (ev.Kind is AgentEventKind.Result or AgentEventKind.Error) break;
        }
    }

    /// <summary>Sobe o app-server e a thread na primeira chamada; as seguintes reaproveitam.</summary>
    private async Task EnsureThreadAsync(AgentRequest request, CancellationToken ct)
    {
        if (_process is { HasExited: false } && _threadId != null && _cwd == request.WorkingDirectory) return;

        var psi = new ProcessStartInfo(ExecutablePath!)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in Mcp?.CodexOverrides() ?? []) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("app-server");
        _process = Process.Start(psi) ?? throw new InvalidOperationException("codex app-server");
        _stderrTail = "";
        var proc = _process;
        _ = Task.Run(async () =>
        {
            // Drena o stderr guardando o fim: versão antiga sai com "unrecognized subcommand" ali.
            while (await proc.StandardError.ReadLineAsync() is { } l)
                if (l.Trim().Length > 0) { var s = _stderrTail + "\n" + l; _stderrTail = s.Length > 1500 ? s[^1500..] : s; }
        });
        _ = Task.Run(ReadLoopAsync);

        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "nxpilot", ["title"] = "NXPilot", ["version"] = "0.1" },
        }, ct);
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized" });

        var started = await RequestAsync("thread/start", new JsonObject
        {
            ["cwd"] = request.WorkingDirectory,
            ["model"] = request.Model ?? model,
            ["approvalPolicy"] = "untrusted",    // pede aprovação para tudo que não for leitura conhecida
            ["sandbox"] = "workspace-write",     // e nunca escreve fora do worktree
            ["ephemeral"] = true,
        }, ct);
        _threadId = started?["thread"]?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("thread/start sem id");
        _threadTokens = _turnStartTokens = new(0, 0); // thread nova: o total do Codex recomeça do zero
        _cwd = request.WorkingDirectory;
        _events?.Writer.TryWrite(new AgentEvent(AgentEventKind.Started, $"Codex · {started?["model"]} · {_cwd}", SessionId: _threadId));
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_process != null && await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (JsonException) { continue; }
                if (msg is not JsonObject obj) continue;

                var method = obj["method"]?.GetValue<string>();
                var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode != null;
                if (method != null && hasId) _ = HandleServerRequestAsync(obj, method);          // pedido do Codex → nós respondemos
                else if (method != null) HandleNotification(method, obj["params"]);
                else if (hasId && _pending.TryRemove(idNode!.GetValue<int>(), out var tcs))     // resposta a um pedido nosso
                {
                    if (obj["error"] is { } err) tcs.TrySetException(new InvalidOperationException(err["message"]?.ToString() ?? err.ToJsonString()));
                    else tcs.TrySetResult(obj["result"]);
                }
            }
        }
        catch { /* processo encerrado */ }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException(NxText.T("o Codex encerrou", "Codex exited", "Codex terminó")));
        await Task.Delay(200); // o stderr costuma chegar junto do fim do stdout
        _events?.Writer.TryWrite(new AgentEvent(AgentEventKind.Error, NxText.T("O processo do Codex terminou.", "The Codex process ended.", "El proceso de Codex terminó.")
            + StderrNote() + OutdatedAgent.HintIfOutdated("Codex", ExecutablePath, _stderrTail), IsError: true));
    }

    private volatile string _stderrTail = "";

    private string StderrNote() => _stderrTail.Trim() is { Length: > 0 } t ? " " + t : "";

    /// <summary>
    /// Total de tokens da thread na notificação de uso (formato tolerante: camelCase ou
    /// snake_case, dentro de "tokenUsage"/"total" ou direto). Entrada do Codex inclui o cache lido.
    /// </summary>
    internal static TokenUsage? ReadTotalTokens(JsonNode p)
    {
        var node = p["tokenUsage"] ?? p["token_usage"] ?? p["usage"] ?? p;
        node = node["total"] ?? node["total_token_usage"] ?? node;
        long N(params string[] names)
        {
            foreach (var n in names)
                if (node[n] is JsonValue v && v.TryGetValue<long>(out var x)) return x;
            return 0;
        }
        var input = N("inputTokens", "input_tokens");
        var cached = N("cachedInputTokens", "cached_input_tokens");
        var output = N("outputTokens", "output_tokens");
        return input + output == 0 ? null : new TokenUsage(Math.Max(0, input - cached), output, cached);
    }

    private void HandleNotification(string method, JsonNode? p)
    {
        var w = _events?.Writer;
        if (w == null || p == null) return;
        switch (method)
        {
            case "item/started" when p["item"] is JsonObject item:
                if (item["id"]?.GetValue<string>() is { } id) _items[id] = item;
                switch (item["type"]?.GetValue<string>())
                {
                    case "commandExecution":
                        w.TryWrite(new AgentEvent(AgentEventKind.ToolUse, "Bash: " + Unwrap(item["command"]?.ToString() ?? "?"), "Bash", _threadId));
                        break;
                    case "fileChange":
                        foreach (var (tool, path) in Changes(item))
                            w.TryWrite(new AgentEvent(AgentEventKind.ToolUse, $"{tool}: {path}", tool, _threadId));
                        break;
                }
                break;
            case "item/completed" when p["item"] is JsonObject item:
                switch (item["type"]?.GetValue<string>())
                {
                    case "agentMessage" when item["text"]?.GetValue<string>() is { Length: > 0 } text:
                        _lastMessage = text;
                        w.TryWrite(new AgentEvent(AgentEventKind.Text, text, SessionId: _threadId));
                        break;
                    case "commandExecution":
                        var output = item["aggregatedOutput"]?.ToString() ?? "";
                        var exit = item["exitCode"]?.ToString();
                        w.TryWrite(new AgentEvent(AgentEventKind.ToolResult, (output.Length > 240 ? output[..240] + "…" : output).Trim(),
                            IsError: exit is not null and not "0"));
                        break;
                }
                break;
            case "turn/completed":
                var status = p["turn"]?["status"]?.GetValue<string>();
                var turnTokens = _threadTokens - _turnStartTokens;
                w.TryWrite(status == "completed"
                    ? new AgentEvent(AgentEventKind.Result, _lastMessage ?? "", SessionId: _threadId, Tokens: turnTokens.Total > 0 ? turnTokens : null)
                    : new AgentEvent(AgentEventKind.Error, NxText.T($"Turno do Codex terminou como \"{status}\". ", $"Codex turn ended as \"{status}\". ", $"El turno de Codex terminó como \"{status}\". ")
                        + (p["turn"]?["error"]?["message"]?.ToString() ?? ""), IsError: true));
                break;
            case "error":
                w.TryWrite(new AgentEvent(AgentEventKind.Stderr, p["error"]?["message"]?.ToString() ?? p.ToJsonString()));
                break;
            case var m when m.Contains("tokenUsage", StringComparison.OrdinalIgnoreCase):
                if (ReadTotalTokens(p) is { } total) _threadTokens = total;
                break;
        }
    }

    /// <summary>Pedido de aprovação do Codex → DecisionGate → "accept"/"decline".</summary>
    private async Task HandleServerRequestAsync(JsonObject msg, string method)
    {
        var p = msg["params"];
        var allow = false;
        try
        {
            switch (method)
            {
                case "item/commandExecution/requestApproval":
                    var cmd = Unwrap(p?["command"]?.ToString() ?? "");
                    allow = (await gate(new GateRequest("Bash", "Bash: " + cmd, _cwd, _threadId))).Allow;
                    break;
                case "item/fileChange/requestApproval":
                    // Os caminhos vêm no item (item/started), não no pedido.
                    var changes = p?["itemId"]?.GetValue<string>() is { } id && _items.TryGetValue(id, out var item) ? Changes(item) : new();
                    if (_mode == AgentMode.Plan || changes.Count == 0)
                    {
                        allow = false; // planejar só lê; alteração sem caminho conhecido também não passa
                        break;
                    }
                    allow = true;
                    foreach (var (tool, path) in changes)
                        allow &= (await gate(new GateRequest(tool, $"{tool}: {path}", _cwd, _threadId))).Allow;
                    break;
                // MCP NXPilot: o gate fica DENTRO do servidor (cada ferramenta pede aprovação no painel).
                case var m when m.Contains("mcp", StringComparison.OrdinalIgnoreCase) && (p?.ToJsonString().Contains("\"" + NXPilot.Mcp.McpServer.ServerName + "\"") ?? false):
                    allow = true;
                    break;
                default:
                    allow = false; // permissões extras, elicitação de outros MCP etc.: não concedidas pelo NXPilot
                    break;
            }
        }
        catch { allow = false; }

        await SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = msg["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["decision"] = allow ? "accept" : "decline" },
        });
    }

    /// <summary>fileChange → (ferramenta, caminho) no formato do gate: add = Write, update = Edit, delete = Delete.</summary>
    private static List<(string Tool, string Path)> Changes(JsonNode item)
    {
        var list = new List<(string, string)>();
        if (item["changes"] is JsonArray changes)
            foreach (var c in changes)
            {
                var kind = c?["kind"] is JsonObject k ? k["type"]?.ToString() : c?["kind"]?.ToString();
                var tool = kind switch { "add" => "Write", "delete" => "Delete", _ => "Edit" };
                list.Add((tool, c?["path"]?.ToString() ?? "?"));
            }
        return list;
    }

    /// <summary>No Windows o Codex embrulha em `pwsh.exe -Command '...'`: o gate classifica o comando de dentro.</summary>
    public static string Unwrap(string command)
    {
        var m = Regex.Match(command, @"-Command\s+(?:'(?<c>.*)'|""(?<c>.*)""|(?<c>.+))\s*$", RegexOptions.Singleline);
        return m.Success ? m.Groups["c"].Value.Replace("''", "'") : command;
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
    }

    private async Task SendAsync(JsonObject message)
    {
        if (_process is not { HasExited: false }) throw new InvalidOperationException(NxText.T("o Codex não está rodando", "Codex is not running", "Codex no se está ejecutando"));
        await _write.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString());
            await _process.StandardInput.FlushAsync();
        }
        finally { _write.Release(); }
    }

    private async Task InterruptAsync()
    {
        try
        {
            if (_threadId != null) await RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = _threadId }, CancellationToken.None);
        }
        catch { /* já parou */ }
        _events?.Writer.TryWrite(new AgentEvent(AgentEventKind.Error, NxText.T("Interrompido pelo usuário.", "Stopped by the user.", "Interrumpido por el usuario."), IsError: true));
    }

    public void Dispose()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* já saiu */ }
        _process?.Dispose();
    }
}
