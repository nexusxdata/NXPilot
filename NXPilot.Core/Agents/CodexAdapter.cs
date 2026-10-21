// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace NXPilot.Agents;

/// <summary>
/// Codex em modo não interativo: `codex exec --json --sandbox read-only`.
///
/// Papel atual: REVISOR. O sandbox somente leitura do próprio Codex impede qualquer escrita,
/// então não precisa do portão de ferramentas (o Codex não tem hook PreToolUse como o Claude).
/// Executar com escrita pelo Codex exigirá o protocolo de aprovação do app-server — por isso
/// o modo Execute é recusado aqui, em vez de rodar sem o DecisionGate.
/// </summary>
public sealed class CodexAdapter(string? model = null, string? executablePath = null) : IAgentAdapter
{
    public string Name => "Codex";

    public string? ExecutablePath { get; } = executablePath ?? Locate();

    /// <summary>JSON Schema da resposta final (--output-schema); nulo = texto livre.</summary>
    public string? OutputSchema { get; init; }

    /// <summary>PATH → instalador oficial (…\Programs\OpenAI\Codex\bin, usado também pelo NXData.AgentInstaller) → pasta do NXProject-Setup → codex.exe da extensão do VS Code (a mais nova).</summary>
    public static string? Locate()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "codex.exe"))
            .Append(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"))
            .Append(Path.Combine(local, "NXProject", "bin", "codex.exe"));
        if (candidates.FirstOrDefault(File.Exists) is { } found) return found;

        var extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        return Directory.Exists(extensions)
            ? Directory.EnumerateDirectories(extensions, "openai.chatgpt-*")
                .Select(d => Path.Combine(d, "bin", "windows-x86_64", "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ExecutablePath == null)
        {
            yield return new AgentEvent(AgentEventKind.Error, NxText.T("Codex não encontrado no Windows.", "Codex not found on Windows.", "Codex no se encontró en Windows."), IsError: true);
            yield break;
        }
        if (request.Mode != AgentMode.Plan)
        {
            yield return new AgentEvent(AgentEventKind.Error, NxText.T(
                "O Codex roda só em modo somente leitura no NXPilot (revisão). Execução com escrita virá pelo protocolo de aprovação.",
                "In NXPilot, Codex only runs read-only (review). Execution with writes will come through the approval protocol.",
                "En NXPilot, Codex solo se ejecuta en modo de solo lectura (revisión). La ejecución con escritura vendrá por el protocolo de aprobación."), IsError: true);
            yield break;
        }

        var blocked = false;
        await foreach (var ev in AgentLogin.EnsureAsync("Codex", ExecutablePath, ct)) { blocked |= ev.IsError; yield return ev; }
        if (blocked) yield break;

        string? schemaPath = null;
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "exec", "--json", "--sandbox", "read-only", "--ephemeral", "--skip-git-repo-check", "-C", request.WorkingDirectory })
            psi.ArgumentList.Add(a);
        var m = request.Model ?? model;
        if (!string.IsNullOrWhiteSpace(m)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(m); }
        if (OutputSchema != null)
        {
            schemaPath = Path.Combine(Path.GetTempPath(), $"nxpilot-schema-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(schemaPath, OutputSchema, ct);
            psi.ArgumentList.Add("--output-schema");
            psi.ArgumentList.Add(schemaPath);
        }
        psi.ArgumentList.Add("-"); // prompt pelo stdin

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.StandardInput.WriteAsync(request.Prompt);
        process.StandardInput.Close();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var killOnCancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* já saiu */ } });

        var sawEnd = false;
        string? lastMessage = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            foreach (var ev in CodexStreamParser.Parse(line, ref lastMessage))
            {
                sawEnd |= ev.Kind is AgentEventKind.Result or AgentEventKind.Error;
                yield return ev;
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;
        if (schemaPath != null) try { File.Delete(schemaPath); } catch { /* temporário */ }

        if (!sawEnd)
            yield return new AgentEvent(AgentEventKind.Error,
                NxText.T($"Codex terminou sem resultado (código {process.ExitCode}). ", $"Codex ended without a result (exit code {process.ExitCode}). ", $"Codex terminó sin resultado (código {process.ExitCode}). ") + stderr.Trim() + OutdatedAgent.HintIfOutdated("Codex", ExecutablePath, stderr), IsError: true);
    }
}

/// <summary>
/// Eventos do `codex exec --json` (JSONL): thread.started, item.completed (agent_message,
/// command_execution, error), turn.completed, turn.failed, error. Leitura tolerante.
/// </summary>
public static class CodexStreamParser
{
    public static IEnumerable<AgentEvent> Parse(string line, ref string? lastMessage)
    {
        var result = new List<AgentEvent>();
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return result;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return result; }
        using (doc)
        {
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "thread.started":
                    result.Add(new AgentEvent(AgentEventKind.Started, "Codex", SessionId: Str(root, "thread_id"), Raw: line));
                    break;
                case "item.completed" when root.TryGetProperty("item", out var item):
                    switch (Str(item, "type"))
                    {
                        case "agent_message" when Str(item, "text") is { } text:
                            lastMessage = text;
                            result.Add(new AgentEvent(AgentEventKind.Text, text));
                            break;
                        case "command_execution":
                            result.Add(new AgentEvent(AgentEventKind.ToolUse, "Bash: " + (Str(item, "command") ?? "?"), "Bash"));
                            break;
                        case "error" when Str(item, "message") is { } warn && !warn.Contains("metadata", StringComparison.OrdinalIgnoreCase):
                            result.Add(new AgentEvent(AgentEventKind.Stderr, warn));
                            break;
                    }
                    break;
                case "turn.completed":
                    result.Add(new AgentEvent(AgentEventKind.Result, lastMessage ?? "", Raw: line));
                    break;
                case "turn.failed":
                    var msg = root.TryGetProperty("error", out var e) ? Str(e, "message") : null;
                    result.Add(new AgentEvent(AgentEventKind.Error, FriendlyError(msg ?? line), IsError: true, Raw: line));
                    break;
            }
        }
        return result;
    }

    /// <summary>O erro mais comum na prática: modelo do config.toml não aceito pela conta ChatGPT.</summary>
    private static string FriendlyError(string raw) =>
        raw.Contains("not supported when using Codex with a ChatGPT account", StringComparison.OrdinalIgnoreCase)
            ? NxText.T("O modelo do Codex não é aceito pela sua conta ChatGPT (o config.toml pode apontar para um modelo que a conta não tem). " +
                       "Na Nova tarefa, escolha um modelo da lista \"Modelo do Codex\" — ela vem da sua conta; testado com o NXPilot: " + string.Join(", ", CodexAppServerAdapter.TestedModels) + ". Detalhe: ",
                       "This Codex model is not accepted by your ChatGPT account (config.toml may point to a model the account lacks). " +
                       "In New task, pick a model from the \"Codex model\" list — it comes from your account; tested with NXPilot: " + string.Join(", ", CodexAppServerAdapter.TestedModels) + ". Detail: ",
                       "Su cuenta de ChatGPT no acepta este modelo de Codex (el config.toml puede apuntar a un modelo que la cuenta no tiene). " +
                       "En Nueva tarea, elija un modelo de la lista \"Modelo de Codex\": proviene de su cuenta; probado con NXPilot: " + string.Join(", ", CodexAppServerAdapter.TestedModels) + ". Detalle: ") + raw
            : raw;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
