// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;
using NXPilot.Mcp;

namespace NXPilot.Agents;

/// <summary>
/// Como o agente sobe a ponte do MCP NXPilot: `NxPilot.exe --mcp`, com o pipe do app no
/// ambiente. O Claude recebe um --mcp-config temporário; o Codex, overrides -c.
/// </summary>
public sealed record McpLaunch(string Executable, string PipeName)
{
    public const string Argument = "--mcp";

    /// <summary>Arquivo temporário do --mcp-config (apagado no fim da execução).</summary>
    public string WriteClaudeConfig()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "run");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ClaudeConfigJson());
        return path;
    }

    public string ClaudeConfigJson() => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [McpServer.ServerName] = new
            {
                type = "stdio",
                command = Executable,
                args = new[] { Argument },
                env = new Dictionary<string, string> { [McpPipe.PipeEnvVar] = PipeName },
            },
        },
    });

    /// <summary>Overrides de configuração do Codex (TOML): string literal '...' evita escapar as barras do Windows.</summary>
    public IEnumerable<string> CodexOverrides() =>
    [
        "-c", $"mcp_servers.{McpServer.ServerName}.command='{Executable}'",
        "-c", $"mcp_servers.{McpServer.ServerName}.args=[\"{Argument}\"]",
        "-c", $"mcp_servers.{McpServer.ServerName}.env={{{McpPipe.PipeEnvVar}=\"{PipeName}\"}}",
    ];
}
