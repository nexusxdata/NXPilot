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
/// Login do agente antes da tarefa, como no VS Code: sem login, abre o login oficial do próprio
/// agente ("claude auth login" / "codex login") numa janela visível — ele leva ao navegador — e
/// a tarefa continua quando você termina. O NXPilot nunca vê a senha nem o token.
/// </summary>
public static class AgentLogin
{
    /// <summary>Evento de login (vai para o Terminal e para o Log da tarefa).</summary>
    public const string Tool = "login";

    /// <summary>
    /// Confere o login e, sem ele, abre o login e espera. Devolve os avisos; um evento IsError
    /// significa que continuou sem login e a tarefa não deve seguir.
    /// </summary>
    public static async IAsyncEnumerable<AgentEvent> EnsureAsync(string agent, string exe, [EnumeratorCancellation] CancellationToken ct)
    {
        var claude = agent.StartsWith("Claude", StringComparison.OrdinalIgnoreCase);
        if (await IsLoggedInAsync(claude, exe, ct) != false) yield break;

        var login = claude ? "claude auth login" : "codex login";
        yield return new AgentEvent(AgentEventKind.Stderr, NxText.T(
            $"{agent} sem login: abrindo o login no navegador ({login}). Conclua lá e a tarefa continua.",
            $"{agent} is not signed in: opening the sign-in page in the browser ({login}). Finish there and the task continues.",
            $"{agent} sin inicio de sesión: abriendo el inicio de sesión en el navegador ({login}). Termine allí y la tarea continúa."), ToolName: Tool);
        await LoginAsync(exe, claude ? ["auth", "login"] : ["login"], ct);

        if (await IsLoggedInAsync(claude, exe, ct) == false)
            yield return new AgentEvent(AgentEventKind.Error, NxText.T(
                $"{agent} continua sem login. Faça o login (janela aberta pelo NXPilot ou '{login}' num terminal) e tente novamente.",
                $"{agent} is still not signed in. Sign in (window opened by NXPilot or '{login}' in a terminal) and try again.",
                $"{agent} sigue sin inicio de sesión. Inicie sesión (ventana abierta por NXPilot o '{login}' en una terminal) e intente de nuevo."), ToolName: Tool, IsError: true);
        else
            yield return new AgentEvent(AgentEventKind.Stderr, NxText.T($"{agent}: login concluído.", $"{agent}: signed in.", $"{agent}: sesión iniciada."), ToolName: Tool);
    }

    /// <summary>
    /// true/false; nulo = não deu para saber (segue como antes). Só chama o subcomando se o --help
    /// o lista: nas versões sem ele, "claude auth ..." / "codex login ..." podem virar PROMPT.
    /// </summary>
    public static Task<bool?> IsLoggedInAsync(bool claude, string exe, CancellationToken ct) => Task.Run<bool?>(() =>
    {
        var help = Run(exe, ["--help"], TimeSpan.FromSeconds(15))?.Output ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(help, claude ? @"^\s+auth\b" : @"^\s+login\b", System.Text.RegularExpressions.RegexOptions.Multiline))
            return null;
        if (claude)
        {
            try
            {
                using var doc = JsonDocument.Parse(Run(exe, ["auth", "status", "--json"], TimeSpan.FromSeconds(20))?.Output ?? "");
                return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            }
            catch { return null; }
        }
        // "codex login status": código 0 = "Logged in using ...", "Not logged in" = sem login.
        if (Run(exe, ["login", "status"], TimeSpan.FromSeconds(20)) is not { } r) return null;
        if (r.Output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)) return false;
        return r.ExitCode == 0 ? true : null;
    }, ct);

    /// <summary>Abre o login oficial do agente (o mesmo de <see cref="EnsureAsync"/>) e espera terminar.</summary>
    public static Task OpenLoginAsync(bool claude, string exe, CancellationToken ct) =>
        LoginAsync(exe, claude ? ["auth", "login"] : ["login"], ct);

    /// <summary>Login oficial numa janela visível: abre o navegador e espera o fim (até 10 min).</summary>
    private static async Task LoginAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ } }
    }

    /// <summary>Roda sem janela e devolve stdout+stderr e o código; nulo se não iniciou ou passou do tempo.</summary>
    internal static (string Output, int ExitCode)? Run(string exe, string[] args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(entireProcessTree: true); } catch { /* já saiu */ } return null; }
            return (output.Result + "\n" + err.Result, p.ExitCode);
        }
        catch { return null; }
    }
}
