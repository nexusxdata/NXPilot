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
/// Claude Code em modo não interativo: `claude -p --output-format stream-json --verbose`.
///
/// O portão é o hook PreToolUse (mecanismo documentado): antes de CADA ferramenta o Claude
/// roda <paramref name="hookCommand"/>, que pergunta ao NXPilot pelo pipe
/// <paramref name="gatePipeName"/>. Com `--permission-prompts none`, qualquer coisa que o
/// hook não liberar é negada — falha no hook nunca vira permissão. Versão sem a opção não roda
/// (pede para atualizar). Sem login, abre o "claude auth login" no navegador antes da tarefa.
/// </summary>
public sealed class ClaudeCodeAdapter(string hookCommand, string gatePipeName, string? executablePath = null) : IAgentAdapter
{
    public string Name => "Claude Code";

    /// <summary>MCP NXPilot desta execução (nulo = parado em Configurar: o Claude sobe sem ele).</summary>
    public McpLaunch? Mcp { get; set; }

    public string? ExecutablePath { get; } = executablePath ?? Locate();

    /// <summary>
    /// Onde procurar o claude.exe nativo do Windows: PATH, ~/.local/bin (instalador oficial),
    /// pasta do NXProject-Setup e, por fim, o binário que vem na extensão do VS Code (a mais nova).
    /// </summary>
    public static string? Locate()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
        candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject", "bin", "claude.exe"));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null) return found;

        var extensions = Path.Combine(home, ".vscode", "extensions");
        return Directory.Exists(extensions)
            ? Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
                .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault()
            : null;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ExecutablePath == null)
        {
            yield return new AgentEvent(AgentEventKind.Error, NxText.T(
                "Claude Code não encontrado no Windows. Instale com: irm https://claude.ai/install.ps1 | iex",
                "Claude Code not found on Windows. Install it with: irm https://claude.ai/install.ps1 | iex",
                "Claude Code no se encontró en Windows. Instálelo con: irm https://claude.ai/install.ps1 | iex"), IsError: true);
            yield break;
        }

        if (!SupportsPermissionPrompts(ExecutablePath))
        {
            // Sem "--permission-prompts none" não há garantia explícita de que o que o hook não
            // liberar seja negado: não roda — pede para atualizar.
            var version = NXData.AgentInstaller.AgentLocator.Version(ExecutablePath) ?? "?";
            yield return new AgentEvent(AgentEventKind.Error, NxText.T(
                $"Claude Code {version} é antigo demais para o NXPilot (falta a opção --permission-prompts, que garante que só o gate libera as ações). Atualize: no NXPilot-Setup, botão Atualizar do Claude Code, ou rode 'claude update'. Executável: {ExecutablePath}",
                $"Claude Code {version} is too old for NXPilot (it lacks the --permission-prompts option, which ensures only the gate allows actions). Update it: in NXPilot-Setup, Claude Code's Update button, or run 'claude update'. Executable: {ExecutablePath}",
                $"Claude Code {version} es demasiado antiguo para NXPilot (le falta la opción --permission-prompts, que garantiza que solo el gate permite las acciones). Actualícelo: en NXPilot-Setup, botón Actualizar de Claude Code, o ejecute 'claude update'. Ejecutable: {ExecutablePath}"), IsError: true);
            yield break;
        }
        var blocked = false;
        await foreach (var ev in AgentLogin.EnsureAsync(Name, ExecutablePath, ct)) { blocked |= ev.IsError; yield return ev; }
        if (blocked) yield break;

        var settingsPath = WriteHookSettings();
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
            // Sem isto o stdin usa a página de código do console e os acentos do prompt chegam errados.
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--settings", settingsPath })
            psi.ArgumentList.Add(a);
        if (request.Mode == AgentMode.Plan) { psi.ArgumentList.Add("--permission-mode"); psi.ArgumentList.Add("plan"); }
        if (request.ResumeSessionId is { } resume) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(resume); }
        if (!string.IsNullOrWhiteSpace(request.Model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(request.Model); }
        // MCP NXPilot: só ele nesta tarefa (--strict-mcp-config ignora outros MCP do usuário).
        var mcpPath = Mcp?.WriteClaudeConfig();
        if (mcpPath != null) foreach (var a in new[] { "--mcp-config", mcpPath, "--strict-mcp-config" }) psi.ArgumentList.Add(a);
        psi.Environment[GatePipe.PipeEnvVar] = gatePipeName;

        using var process = new Process { StartInfo = psi };
        process.Start();
        // Prompt pelo stdin: evita limite e aspas da linha de comando do Windows.
        await process.StandardInput.WriteAsync(request.Prompt);
        process.StandardInput.Close();

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } l) stderr.AppendLine(l);
        });

        using var killOnCancel = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* já saiu */ } });
        var sawResult = false;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            foreach (var ev in ClaudeStreamParser.Parse(line))
            {
                sawResult |= ev.Kind is AgentEventKind.Result or AgentEventKind.Error;
                yield return ev;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await stderrTask;
        TryDelete(settingsPath);
        if (mcpPath != null) TryDelete(mcpPath);

        if (ct.IsCancellationRequested)
            yield return new AgentEvent(AgentEventKind.Error, NxText.T("Interrompido pelo usuário.", "Stopped by the user.", "Interrumpido por el usuario."), IsError: true);
        else if (!sawResult)
            yield return new AgentEvent(AgentEventKind.Error,
                NxText.T($"Claude Code terminou sem resultado (código {process.ExitCode}). ", $"Claude Code ended without a result (exit code {process.ExitCode}). ", $"Claude Code terminó sin resultado (código {process.ExitCode}). ")
                + stderr.ToString().Trim() + OutdatedAgent.HintIfOutdated(Name, ExecutablePath, stderr.ToString()), IsError: true);
    }

    private static readonly Dictionary<string, bool> PermissionPromptsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Este claude.exe aceita --permission-prompts? Lido do --help, uma vez por binário e data.</summary>
    private static bool SupportsPermissionPrompts(string exe)
    {
        var key = $"{exe}|{File.GetLastWriteTimeUtc(exe):O}";
        lock (PermissionPromptsCache) if (PermissionPromptsCache.TryGetValue(key, out var c)) return c;
        var help = AgentLogin.Run(exe, ["--help"], TimeSpan.FromSeconds(15))?.Output ?? "";
        // --help sem resposta não prova versão antiga: segue com a opção (e não guarda no cache).
        if (!help.Contains("--print")) return true;
        var ok = help.Contains("--permission-prompts");
        lock (PermissionPromptsCache) PermissionPromptsCache[key] = ok;
        return ok;
    }

    /// <summary>Settings temporário só com o hook: matcher "*" = toda ferramenta passa pelo gate.</summary>
    private string WriteHookSettings()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "run");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"hooks-{Guid.NewGuid():N}.json");
        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    // timeout em segundos: o hook espera a SUA decisão no painel (até 15 min).
                    new { matcher = "*", hooks = new[] { new { type = "command", command = hookCommand, timeout = 900 } } }
                }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(settings));
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* arquivo temporário: some no próximo uso */ }
    }
}
