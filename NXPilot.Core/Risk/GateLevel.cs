// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.RegularExpressions;

namespace NXPilot.Risk;

/// <summary>
/// Nível do gate por tarefa — o próprio gate do NXPilot decide, com auditoria (nunca um modo do agente que passe
/// por fora). Padrão: BAIXO passa sozinho. Rigoroso: até o BAIXO pede (leitura pura continua passando).
/// Confiante: MÉDIO local (comando ou arquivo) também passa — bom para clone de tarefa já aprovada. ALTO sempre pede.
/// </summary>
public enum GateLevel { Padrao, Rigoroso, Confiante }

public static class GateLevelPolicy
{
    // Sai do computador ou mexe em sistema de outras pessoas: no Confiante continua pedindo.
    private static readonly Regex ExternalCommand = new(
        @"\b(git\s+push|curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr|irm|scp|ftp|kubectl|az\s|aws\s|gcloud|terraform|npm\s+publish|dotnet\s+nuget\s+push)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReadCommand = new(
        @"^\s*(git\s+(status|log|diff|show|branch)|Get-ChildItem|Get-Content|Get-Location|Select-String|Test-Path|ls|dir|cat|type|rg|grep|pwd)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Confiante: MÉDIO de ação local passa sozinho. Ferramentas externas (MCP: Databricks, Git, e-mail, Teams…), rede e ALTO não.</summary>
    public static bool AutoApprovesMedium(GateLevel level, string action, RiskLevel risk) =>
        level == GateLevel.Confiante && risk == RiskLevel.Medio && !IsExternal(action);

    /// <summary>Rigoroso: o BAIXO também vai para o painel, menos a leitura pura (ler arquivo, listar, git status).</summary>
    public static bool RequiresApproval(GateLevel level, string action, RiskLevel risk) =>
        level == GateLevel.Rigoroso && risk == RiskLevel.Baixo && !IsReadOnly(action);

    private static bool IsExternal(string action) =>
        action.StartsWith("MCP", StringComparison.OrdinalIgnoreCase) || action.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)
        || action.StartsWith("Databricks", StringComparison.OrdinalIgnoreCase) || action.StartsWith("WebFetch", StringComparison.OrdinalIgnoreCase)
        || ExternalCommand.IsMatch(action);

    private static bool IsReadOnly(string action)
    {
        var i = action.IndexOf(':');
        var tool = i > 0 ? action[..i].Trim() : "";
        var body = i > 0 ? action[(i + 1)..] : action;
        return tool is "Read" or "Glob" or "Grep" or "LS" or "ToolSearch" || (tool is "Bash" or "PowerShell" && ReadCommand.IsMatch(body));
    }
}
