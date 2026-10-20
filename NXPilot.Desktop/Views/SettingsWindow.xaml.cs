// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXPilot.Desktop.Services;
using NXPilot.Updates;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Configurar → Versão: verificar atualização, voltar para uma versão anterior e avisar quando
/// a base (Setup) mudou. Atualizar = baixar o NXPilot-Release.zip, conferir o SHA-256, trocar os
/// arquivos (renomeando os em uso, sem script) e reiniciar o app.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly UpdateService _updates = new();
    private static string AppDir => AppContext.BaseDirectory;

    public SettingsWindow()
    {
        InitializeComponent();
        var v = UpdateService.CurrentVersion;
        CurrentVersionText.Text = T($"Instalada: NXPilot {v.ToString(3)}.{v.Revision:00}", $"Installed: NXPilot {v.ToString(3)}.{v.Revision:00}", $"Instalada: NXPilot {v.ToString(3)}.{v.Revision:00}");
        Loaded += async (_, _) => await LoadVersionsAsync();
        Action refresh = () => Dispatcher.BeginInvoke(RefreshMcp);
        McpHost.Changed += refresh;
        Closed += (_, _) => McpHost.Changed -= refresh;
        RefreshMcp();
    }

    // ------------------------------------------------------------ MCP NXPilot

    private void OnMcpToggleClick(object sender, RoutedEventArgs e)
    {
        if (McpHost.Running) McpHost.Stop(); else McpHost.Start();
    }

    /// <summary>Estado, quantas ferramentas o agente vê e a lista por extensão (configurada ou não).</summary>
    private void RefreshMcp()
    {
        var on = McpHost.Running;
        McpDot.Fill = on ? Brushes.SeaGreen : Brushes.Gray;
        McpButton.Content = on ? T("Parar", "Stop", "Detener") : T("Iniciar", "Start", "Iniciar");

        // Sem tarefa: Power BI depende do worktree (PBIP), então conta como "disponível quando houver".
        var probe = new NXPilot.Mcp.ToolContext { Worktree = Path.GetTempPath(), Approve = (_, _, _) => Task.FromResult(false) };
        var available = McpHost.Catalog.AvailableTools(probe).Select(t => t.Name).ToHashSet();
        bool Ready(NXPilot.Mcp.NxTool t) => available.Contains(t.Name) || (t.Extension == "Power BI" && PowerBiReady(t));
        var count = McpHost.Catalog.Tools.Count(Ready);
        var last = McpHost.LastCall is { } call ? T($" · última chamada {call}", $" · last call {call}", $" · última llamada {call}") : "";
        McpStatusText.Text = on
            ? T($"Ligado · {count} ferramentas para o agente{last}", $"Running · {count} tools for the agent{last}", $"Activo · {count} herramientas para el agente{last}")
            : T("Parado — as tarefas sobem sem o MCP NXPilot.", "Stopped — tasks start without the MCP NXPilot.", "Detenido — las tareas se inician sin el MCP NXPilot.");

        McpToolsText.Text = string.Join("\n", McpHost.Catalog.Tools.GroupBy(t => t.Extension).Select(g =>
        {
            var ok = g.Any(Ready);
            var note = g.Key == "Power BI" ? T(" (quando o worktree tem um projeto PBIP)", " (when the worktree has a PBIP project)", " (cuando el worktree tiene un proyecto PBIP)")
                : ok ? "" : T(" (não configurado na Extensão MCP)", " (not configured in MCP Extension)", " (no configurado en Extensión MCP)");
            return $"{(ok ? "✓" : "–")} {g.Key}{note}: {string.Join(", ", g.Where(t => !ok || Ready(t)).Select(t => t.Name))}";
        }));
    }

    private static bool PowerBiReady(NXPilot.Mcp.NxTool t) => t.Name != "powerbi_capture" || PowerBiDesktop.Locate() != null;

    private async Task LoadVersionsAsync()
    {
        try
        {
            var versions = await _updates.ListVersionsAsync();
            VersionsBox.Items.Clear();
            foreach (var p in versions)
                VersionsBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{p.Tag}  ·  {p.PublishedAt.LocalDateTime:dd/MM/yyyy}{(p.Version == UpdateService.CurrentVersion ? T("  (instalada)", "  (installed)", "  (instalada)") : "")}",
                    Tag = p,
                });
            if (versions.Count == 0) Status(T("Nenhuma versão com pacote de atualização publicada ainda.", "No version with an update package published yet.", "Aún no hay ninguna versión con paquete de actualización publicada."), Brushes.DimGray);

            var setup = await _updates.GetSetupReleaseAsync();
            SetupBanner.Visibility = setup != null && UpdateService.SetupChanged(UpdateService.InstalledSetupStamp(AppDir), setup.BuiltAt)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e) =>
        InstallVersionButton.IsEnabled = (VersionsBox.SelectedItem as ComboBoxItem)?.Tag is PublishedVersion p && p.Version != UpdateService.CurrentVersion;

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        await Busy(async () =>
        {
            // Como no NXProject: a base vem PRIMEIRO. Se ela mudou, o Setup instala a base e o app mais novo juntos.
            if (await BaseChangedAsync()) { await OfferSetupAsync(); return; }
            var latest = await _updates.CheckForUpdateAsync();
            if (latest == null) { Status(T("Você já está na versão mais nova.", "You are already on the latest version.", "Ya tiene la versión más reciente."), Brushes.Green); return; }
            await InstallAsync(latest, rollback: false);
        });
    }

    /// <summary>A base publicada é mais nova que a instalada (ou a instalação não tem carimbo)?</summary>
    private async Task<bool> BaseChangedAsync()
    {
        var setup = await _updates.GetSetupReleaseAsync();
        return setup != null && UpdateService.SetupChanged(UpdateService.InstalledSetupStamp(AppDir), setup.BuiltAt);
    }

    /// <summary>Base nova: explica e, com o seu OK, segue pelo Setup (atualizar só o app deixaria faltar biblioteca).</summary>
    private async Task OfferSetupAsync()
    {
        SetupBanner.Visibility = Visibility.Visible;
        var ask = T("Esta atualização traz bibliotecas novas na base do NXPilot. Atualizar só o app deixaria faltar arquivos (o app fecharia ao usá-los).\n\nBaixar e abrir o NXPilot-Setup agora? Ele instala a base nova e a versão mais nova do app. O NXPilot vai fechar.",
                    "This update brings new libraries in the NXPilot base. Updating only the app would leave files missing (the app would close when using them).\n\nDownload and open NXPilot-Setup now? It installs the new base and the latest app version. NXPilot will close.",
                    "Esta actualización trae bibliotecas nuevas en la base de NXPilot. Actualizar solo la app dejaría archivos faltantes (la app se cerraría al usarlos).\n\n¿Descargar y abrir NXPilot-Setup ahora? Instala la base nueva y la versión más reciente de la app. NXPilot se cerrará.");
        if (MessageBox.Show(this, ask, "NXPilot", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            Status(T("Atualização adiada: use o botão do Setup quando quiser.", "Update postponed: use the Setup button whenever you want.", "Actualización pospuesta: use el botón del Setup cuando quiera."), Brushes.DimGray);
            return;
        }
        await RunSetupAsync();
    }

    private async void OnInstallVersionClick(object sender, RoutedEventArgs e)
    {
        if ((VersionsBox.SelectedItem as ComboBoxItem)?.Tag is not PublishedVersion p) return;
        var rollback = p.Version < UpdateService.CurrentVersion;
        await Busy(async () =>
        {
            // Subir de versão com a base mudada vai pelo Setup (voltar versão não precisa de base nova).
            if (!rollback && await BaseChangedAsync()) { await OfferSetupAsync(); return; }
            await InstallAsync(p, rollback);
        });
    }

    /// <summary>Confirma, baixa, confere, troca os arquivos e reinicia. Tarefas em andamento são interrompidas.</summary>
    private async Task InstallAsync(PublishedVersion version, bool rollback)
    {
        var question = rollback
            ? T($"Voltar para a {version.Tag}? O NXPilot vai fechar e abrir de novo; tarefas em andamento serão interrompidas.",
                $"Go back to {version.Tag}? NXPilot will close and reopen; running tasks will be interrupted.",
                $"¿Volver a {version.Tag}? NXPilot se cerrará y se volverá a abrir; las tareas en curso se interrumpirán.")
            : T($"Atualizar para a {version.Tag}? O NXPilot vai fechar e abrir de novo; tarefas em andamento serão interrompidas.",
                $"Update to {version.Tag}? NXPilot will close and reopen; running tasks will be interrupted.",
                $"¿Actualizar a {version.Tag}? NXPilot se cerrará y se volverá a abrir; las tareas en curso se interrumpirán.");
        if (MessageBox.Show(this, question, "NXPilot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var package = await _updates.DownloadAsync(version, new Progress<string>(m => Status(m, Brushes.DimGray)), default, BytesProgress());
        await ShowCompleteAsync();
        Status(T("Instalando…", "Installing…", "Instalando…"), Brushes.DimGray);
        var count = await Task.Run(() => UpdateService.Apply(package, AppDir));
        try { Directory.Delete(Path.GetDirectoryName(package)!, true); } catch { /* temporário */ }
        Status(T($"{count} arquivo(s) atualizados. Reiniciando…", $"{count} file(s) updated. Restarting…", $"{count} archivo(s) actualizado(s). Reiniciando…"), Brushes.Green);

        Process.Start(new ProcessStartInfo(Path.Combine(AppDir, "NxPilot.exe")) { WorkingDirectory = AppDir, UseShellExecute = true });
        Application.Current.Shutdown();
    }

    /// <summary>A base mudou (runtime/bibliotecas): baixa o Setup, extrai e abre — ele instala e atualiza o app.</summary>
    private async void OnSetupClick(object sender, RoutedEventArgs e)
    {
        await Busy(RunSetupAsync);
    }

    /// <summary>Baixa o Setup, extrai e abre — ele instala a base e atualiza o app.</summary>
    private async Task RunSetupAsync()
    {
        var setup = await _updates.GetSetupReleaseAsync() ?? throw new UpdateException(T("Setup não encontrado no GitHub.", "Setup not found on GitHub.", "Setup no encontrado en GitHub."));
        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", UpdateService.SetupAsset);
        Status(T("Baixando o Setup…", "Downloading the Setup…", "Descargando el Setup…"), Brushes.DimGray);
        await _updates.DownloadFileAsync(setup.DownloadUrl, zip, BytesProgress());
        await ShowCompleteAsync();
        Status(T("Extraindo e abrindo o Setup…", "Extracting and opening the Setup…", "Extrayendo y abriendo el Setup…"), Brushes.DimGray);
        var dir = Path.Combine(Path.GetTempPath(), "nxpilot-setup-" + Guid.NewGuid().ToString("N"));
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);
        var exe = Directory.GetFiles(dir, "NXPilot-Setup.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new UpdateException(T("O zip do Setup não tem o NXPilot-Setup.exe.", "The Setup zip has no NXPilot-Setup.exe.", "El zip del Setup no contiene NXPilot-Setup.exe."));
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
        Application.Current.Shutdown(); // o Setup encerra o app se ainda estiver aberto
    }

    private async Task Busy(Func<Task> action)
    {
        CheckButton.IsEnabled = InstallVersionButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true; // até o download começar a informar o tamanho
        Progress.Value = 0;
        try { await action(); }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
            OnVersionSelected(this, null!);
        }
    }

    /// <summary>Barra com % e MB enquanto baixa (igual ao NXProject).</summary>
    private Progress<(long Downloaded, long Total)> BytesProgress() => new(b =>
    {
        Progress.IsIndeterminate = b.Total <= 0;
        if (b.Total > 0) Progress.Value = Math.Min(100, b.Downloaded * 100d / b.Total);
        Status(UpdateService.DescribeProgress(b.Downloaded, b.Total), Brushes.DimGray);
    });

    /// <summary>Fim do download: barra cheia e "100%" à vista um instante, para ficar claro que acabou.</summary>
    private async Task ShowCompleteAsync()
    {
        Progress.IsIndeterminate = false;
        Progress.Value = 100;
        Status(StatusText.Text.StartsWith("100%") ? StatusText.Text : T("100% · download concluído", "100% · download complete", "100% · descarga concluida"), Brushes.Green);
        await Task.Delay(700);
    }

    private void Status(string text, Brush color) { StatusText.Text = text; StatusText.Foreground = color; }
}
