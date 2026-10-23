// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text;
using NXPilot.Agents;

namespace NXPilot.Desktop;

/// <summary>
/// Ponto de entrada. `NxPilot.exe --hook` é o hook PreToolUse que o Claude Code chama antes
/// de cada ferramenta: responde pelo stdout SEM abrir janela nem carregar o WPF (precisa ser
/// rápido — roda a cada ação do agente). Sem esse argumento, abre o app normalmente.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--hook")
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            return GatePipe.RunHookAsync(Console.In, Console.Out, Environment.GetEnvironmentVariable(GatePipe.PipeEnvVar))
                .GetAwaiter().GetResult();
        }

        // Ponte do MCP NXPilot (iniciada pelo agente): stdio ↔ pipe do app, sem WPF.
        // Script (API para scripts, Configurar → MCP): `--mcp --workdir <pasta>` acha o app pelo arquivo de descoberta.
        if (args.Length > 0 && args[0] == McpLaunch.Argument)
        {
            var w = Array.IndexOf(args, "--workdir");
            return NXPilot.Mcp.McpPipe.RunBridgeAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                NXPilot.Mcp.McpPipe.FindPipe(), w > 0 && w + 1 < args.Length ? args[w + 1] : null).GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
