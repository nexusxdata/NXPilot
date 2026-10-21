// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using NXPilot.WindowsUi;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Extensão Power BI Desktop, parte de INTERFACE (pacote NXPilot.WindowsUi): abre o .pbip no
/// Power BI Desktop e captura a janela para conferir o resultado. Não clica em nada e não monta
/// visual — isso o agente faz editando TMDL/PBIR. Só enxerga o processo PBIDesktop.
/// Testado em 18/10/2026 com o Power BI da Microsoft Store (2.159).
/// </summary>
public static class PowerBiDesktop
{
    public const string ProcessName = "PBIDesktop";

    /// <summary>Instalação clássica (Program Files) ou alias da versão da Microsoft Store.</summary>
    public static string? Locate()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Power BI Desktop", "bin", "PBIDesktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "PBIDesktopStore.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static readonly Lazy<bool> Installed = new(() => Locate() != null);

    /// <summary>Power BI Desktop instalado (consultado uma vez por execução do app).</summary>
    public static bool IsInstalled => Installed.Value;

    /// <summary>
    /// Tema base mais novo (BaseThemes/CY*.json) da instalação do Power BI Desktop — o mesmo que ele
    /// grava num relatório novo. Copiado da máquina do usuário; o NXPilot não distribui o tema.
    /// </summary>
    public static string? BaseThemeFile()
    {
        foreach (var bin in InstallBinDirs())
        {
            var themes = Path.Combine(bin, "WebView2Resources", "minerva", "sharedresources", "BaseThemes");
            try
            {
                if (Directory.Exists(themes) && Directory.EnumerateFiles(themes, "CY*.json").Order(StringComparer.OrdinalIgnoreCase).LastOrDefault() is { } newest)
                    return newest;
            }
            catch { /* sem acesso: tenta a próxima */ }
        }
        return null;
    }

    /// <summary>Pasta bin da instalação: a clássica, a do processo aberto ou a do pacote da Store.</summary>
    private static IEnumerable<string> InstallBinDirs()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Power BI Desktop", "bin");
        string? running = null;
        try { running = System.Diagnostics.Process.GetProcessesByName(ProcessName).Select(p => p.MainModule?.FileName).FirstOrDefault(f => f != null); }
        catch { /* sem acesso ao processo */ }
        if (running != null) yield return Path.GetDirectoryName(running)!;
        if (StorePackageDir() is { } store) yield return Path.Combine(store, "bin");
    }

    private static string? _storeDir;

    /// <summary>Pasta do pacote da Microsoft Store (a pasta WindowsApps não pode ser listada; pergunta ao Windows).</summary>
    private static string? StorePackageDir()
    {
        if (_storeDir != null) return _storeDir;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name Microsoft.MicrosoftPowerBIDesktop | Sort-Object Version | Select-Object -Last 1).InstallLocation\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var dir = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(15000);
            return _storeDir = string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? null : dir;
        }
        catch { return null; }
    }

    /// <summary>
    /// Janelas de erro do Power BI abertas junto com o projeto ("Something went wrong"…): o relatório
    /// não carregou, mesmo que a janela principal apareça.
    /// </summary>
    public static IReadOnlyList<WindowInfo> ErrorDialogs(WindowsUiService ui, WindowInfo main)
    {
        try { return ui.ChildWindows(main); }
        catch { return []; } // janela fechou durante a leitura
    }

    /// <summary>
    /// Abre o projeto, espera a janela (o título é só o nome do projeto — o Power BI é reconhecido
    /// pelo processo; até 4 min, o Power BI demora a carregar o modelo), dá tempo para os visuais
    /// renderizarem e grava a captura em <paramref name="png"/>. O Power BI continua aberto.
    /// </summary>
    public static async Task<WindowInfo> OpenAndCaptureAsync(string pbipPath, string png, CancellationToken ct = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException(NxText.T(
            "Power BI Desktop não encontrado. Instale pela Microsoft Store ou em https://powerbi.microsoft.com/desktop.",
            "Power BI Desktop not found. Install it from the Microsoft Store or https://powerbi.microsoft.com/desktop.",
            "Power BI Desktop no encontrado. Instálelo desde Microsoft Store o en https://powerbi.microsoft.com/desktop."));
        using var ui = new WindowsUiService(WindowsUiPolicy.PowerBiOnly);
        WindowInfo window;
        try
        {
            window = await ui.LaunchAndWaitAsync(exe, $"\"{pbipPath}\"", ProcessName,
                Path.GetFileNameWithoutExtension(pbipPath), TimeSpan.FromMinutes(4), ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(NxText.T("O Power BI Desktop não mostrou a janela do projeto em 4 minutos.", "Power BI Desktop did not show the project window within 4 minutes.", "Power BI Desktop no mostró la ventana del proyecto en 4 minutos."));
        }
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // modelo carregado → visuais renderizando
        ui.Capture(window, png);
        return window;
    }
}
