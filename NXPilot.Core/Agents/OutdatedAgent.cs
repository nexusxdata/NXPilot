// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.RegularExpressions;

namespace NXPilot.Agents;

/// <summary>
/// Agente antigo demais para o que o NXPilot pede (opção, subcomando ou método do protocolo
/// que ele não conhece): o erro ganha o pedido para atualizar, com versão e executável.
/// </summary>
public static class OutdatedAgent
{
    private static readonly Regex Pattern = new(
        @"unknown option|unknown command|unexpected argument|unrecognized (subcommand|option|argument)|unknown variant|unknown field|method not found|-32601",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Looks(string? text) => text != null && Pattern.IsMatch(text);

    /// <summary>Mensagem de atualizar (vazia se o texto não parece versão antiga).</summary>
    public static string HintIfOutdated(string agent, string? exe, string? errorText) =>
        Looks(errorText) ? " " + Hint(agent, exe) : "";

    public static string Hint(string agent, string? exe)
    {
        var version = exe != null ? NXData.AgentInstaller.AgentLocator.Version(exe) ?? "?" : "?";
        var command = agent.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ? "claude update" : "irm https://chatgpt.com/codex/install.ps1 | iex";
        return NxText.T(
            $"Parece uma versão antiga do {agent} ({version}) que não conhece o que o NXPilot usa. Atualize: no NXPilot-Setup, botão Atualizar do {agent}, ou rode '{command}'. Executável: {exe}",
            $"This looks like an old {agent} version ({version}) that does not know what NXPilot uses. Update it: in NXPilot-Setup, {agent}'s Update button, or run '{command}'. Executable: {exe}",
            $"Parece una versión antigua de {agent} ({version}) que no conoce lo que NXPilot usa. Actualícelo: en NXPilot-Setup, botón Actualizar de {agent}, o ejecute '{command}'. Ejecutable: {exe}");
    }
}
