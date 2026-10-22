// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using NXData.AgentInstaller;

namespace NXPilot.Agents;

/// <summary>
/// Fachada de atualização dos agentes dentro do app: a mesma rotina do NXPilot-Setup (NXData.AgentInstaller,
/// instalação automática da última versão, SHA-256 conferido) — quem já usa o NXPilot dificilmente baixa o
/// Setup de novo só para atualizar o Claude Code ou o Codex.
/// </summary>
public static class AgentUpdates
{
    public sealed record Available(AgentKind Kind, string Installed, string Latest, string Source);

    private static readonly AgentInstallOptions Options = new();
    private static readonly AgentInstaller Installer = new(Options);

    private static AgentCli Cli(AgentKind kind) => kind == AgentKind.Claude ? AgentCli.ClaudeCode : AgentCli.Codex;

    /// <summary>Versão nova publicada para o agente instalado; nula se está em dia, não instalado ou sem internet.</summary>
    public static async Task<Available?> CheckAsync(AgentKind kind, CancellationToken ct = default)
    {
        try
        {
            var found = await Task.Run(() => AgentLocator.Find(Cli(kind), Options), ct);
            if (found?.Version is not { } installed) return null;
            var (latest, _) = await Installer.ResolveVersionAsync(Cli(kind), VersionChoice.Latest, ct);
            return IsNewer(latest, installed) ? new Available(kind, installed, latest, found.Source) : null;
        }
        catch { return null; } // sem internet ou serviço fora: sem aviso
    }

    /// <summary>"0.163.0" é mais nova que "0.162.0-alpha.17.2"? (compara só a parte numérica)</summary>
    public static bool IsNewer(string latest, string installed) =>
        Version.TryParse(latest.Split('-')[0], out var l) && Version.TryParse(installed.Split('-')[0], out var i) && l > i;

    /// <summary>Instala a última versão nas pastas oficiais (o NXPilot passa a usar essa).</summary>
    public static Task<AgentInstallResult> UpdateAsync(AgentKind kind, IProgress<string>? status = null, CancellationToken ct = default) =>
        Installer.InstallAsync(Cli(kind), InstallMode.Internal, VersionChoice.Latest, status, ct);
}
