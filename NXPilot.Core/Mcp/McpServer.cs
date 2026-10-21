// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NXPilot.Mcp;

/// <summary>
/// MCP NXPilot — lado do agente: `NxPilot.exe --mcp` é iniciado pelo Claude Code (ou Codex) e
/// fala MCP por stdio (JSON-RPC, uma mensagem por linha). É só uma PONTE: lista e chamadas de
/// ferramentas vão ao NXPilot aberto pelo pipe <see cref="McpPipe"/>, onde ficam as extensões,
/// os tokens e o gate. Sem o app escutando, nenhuma ferramenta executa (nunca por omissão).
/// </summary>
public static class McpServer
{
    public const string ServerName = "nxpilot";
    private static readonly string[] KnownProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>O que o agente lê ao conectar (instruções do servidor MCP).</summary>
    public const string Instructions =
        "NXPilot tools for this task (Power BI, PowerPoint, Excel, Databricks, Git, e-mail, work items, Teams, Windows apps — only the ones the user configured are listed). " +
        "Paths are relative to the task worktree. " +
        "For PowerPoint ALWAYS use the pptx_* tools: do not write python-pptx/Node scripts and do not install packages for it; after building, call pptx_render to look at the slides and fix what looks bad. " +
        "For Excel use the xlsx_* tools to inspect (xlsx_info, then xlsx_read with a range) and for small or medium edits; a script is fine only for bulk transformations of thousands of rows. " +
        "For a Power BI PBIP project, after editing TMDL/PBIR call powerbi_validate, then powerbi_capture to see the report. " +
        "Every call passes the NXPilot gate: a denial means the user said no — do not retry the same call; adjust the plan or ask.";

    /// <summary>
    /// Laço do servidor: responde initialize/ping aqui; tools/list e tools/call vão para
    /// <paramref name="forward"/> (o pipe do app). Termina quando o stdin fecha.
    /// </summary>
    public static async Task RunAsync(TextReader input, TextWriter output, Func<JsonObject, CancellationToken, Task<JsonObject>> forward,
        CancellationToken ct = default)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { await WriteAsync(Error(null, -32700, "parse error")); continue; }
            if (msg == null) continue;

            var id = msg["id"]?.DeepClone();
            var method = msg["method"]?.GetValue<string>();
            if (method == null || id == null) continue; // notificação (initialized, cancelled) ou resposta: nada a responder

            // Chamadas podem demorar (aprovação, Power BI abrindo): atende em paralelo.
            running.Add(Task.Run(async () => await WriteAsync(await HandleAsync(id, method, msg["params"] as JsonObject)), ct));
            running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);

        async Task WriteAsync(JsonObject response)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await output.WriteLineAsync(response.ToJsonString());
                await output.FlushAsync(ct);
            }
            finally { writeLock.Release(); }
        }

        async Task<JsonObject> HandleAsync(JsonNode id, string method, JsonObject? p)
        {
            switch (method)
            {
                case "initialize":
                    var asked = p?["protocolVersion"]?.GetValue<string>();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = asked != null && KnownProtocols.Contains(asked) ? asked : KnownProtocols[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "MCP NXPilot", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "1.0" },
                        ["instructions"] = Instructions,
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                case "tools/call":
                    try
                    {
                        var reply = await forward(new JsonObject { ["method"] = method, ["params"] = p?.DeepClone() ?? new JsonObject() }, ct);
                        if (reply["error"]?.GetValue<string>() is { } err)
                            return method == "tools/list" ? Result(id, new JsonObject { ["tools"] = new JsonArray() }) : Result(id, ToolResult.Error(err).ToJson());
                        return Result(id, reply["result"]?.DeepClone() as JsonObject ?? new JsonObject());
                    }
                    catch (Exception ex)
                    {
                        var why = NxText.T("MCP NXPilot: o NXPilot não está aberto ou o MCP está parado — nada foi executado. ", "MCP NXPilot: NXPilot is not open or the MCP is stopped — nothing was run. ", "MCP NXPilot: NXPilot no está abierto o el MCP está detenido; no se ejecutó nada. ") + ex.Message;
                        return method == "tools/list" ? Result(id, new JsonObject { ["tools"] = new JsonArray() }) : Result(id, ToolResult.Error(why).ToJson());
                    }
                default:
                    return Error(id, -32601, "method not found: " + method);
            }
        }
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

/// <summary>
/// Pipe local entre a ponte MCP (`--mcp`) e o app: uma linha JSON de ida (method + params), uma
/// de volta (result ou error). Nome na variável <see cref="PipeEnvVar"/>, com o PID do app.
/// </summary>
public static class McpPipe
{
    public const string PipeEnvVar = "NXPILOT_MCP_PIPE";

    public static async Task ServeAsync(string pipeName, Func<JsonObject, CancellationToken, Task<JsonObject>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try { await server.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { await server.DisposeAsync(); break; }
            _ = Task.Run(async () =>
            {
                await using (server)
                {
                    JsonObject reply;
                    try
                    {
                        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                        var line = await reader.ReadLineAsync(ct);
                        reply = line != null && JsonNode.Parse(line) is JsonObject req
                            ? await handle(req, ct)
                            : new JsonObject { ["error"] = "pedido vazio" };
                    }
                    catch (Exception ex) { reply = new JsonObject { ["error"] = "NXPilot: " + ex.Message }; }
                    try
                    {
                        await server.WriteAsync(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n"), ct);
                        await server.FlushAsync(ct);
                    }
                    catch { /* a ponte já desistiu */ }
                }
            }, ct);
        }
    }

    /// <summary>Lado da ponte: manda o pedido e espera a resposta (sem prazo — pode estar esperando a SUA aprovação).</summary>
    public static async Task<JsonObject> RequestAsync(string? pipeName, JsonObject request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pipeName)) throw new InvalidOperationException(NxText.T("sem o pipe do NXPilot (MCP fora do app)", "no NXPilot pipe (MCP outside the app)", "sin el pipe de NXPilot (MCP fuera de la aplicación)"));
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        await client.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), ct);
        await client.FlushAsync(ct);
        using var reader = new StreamReader(client, Encoding.UTF8);
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException(NxText.T("sem resposta do NXPilot", "no answer from NXPilot", "sin respuesta de NXPilot"));
        return JsonNode.Parse(line) as JsonObject ?? throw new IOException("?");
    }

    /// <summary>Corpo de `NxPilot.exe --mcp`: stdio UTF-8 sem BOM ↔ pipe do app.</summary>
    public static async Task<int> RunBridgeAsync(Stream stdin, Stream stdout, string? pipeName)
    {
        using var reader = new StreamReader(stdin, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        await McpServer.RunAsync(reader, writer, (req, ct) => RequestAsync(pipeName, req, ct));
        return 0;
    }
}
