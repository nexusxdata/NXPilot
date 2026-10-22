// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXPilot.Onnx;
using static NXPilot.NxText;

namespace NXPilot.Setup;

/// <summary>
/// Instalador do NXPilot no modelo do NXProject-Setup: traz só a BASE (runtime self-contained e
/// bibliotecas de terceiros) — o NxPilot.exe e as DLLs do NXPilot NÃO vêm nele. O Passo 2 baixa
/// o app (NXPilot-Release.zip da última versão) do GitHub, copia a base e aplica o app por cima.
/// O Setup vive na release fixa nxpilot-setup-latest e só muda quando a base muda.
/// </summary>
public partial class MainWindow : Window
{
    private const string AppExeName = "NxPilot.exe";

    /// <summary>Instalação dos agentes (pacote NXData.AgentInstaller, compartilhado com o NXProject).</summary>
    private readonly NXData.AgentInstaller.AgentInstallOptions _agentOptions = new();
    private readonly NXData.AgentInstaller.AgentInstaller _agents;

    private readonly string _installDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "NXPilot");

    private readonly OnnxModelStore _onnx = new();

    /// <summary>Pasta com a base: a do próprio Setup, ou --payload &lt;pasta&gt; (desenvolvimento).</summary>
    private readonly string _payloadDir;

    public MainWindow()
    {
        InitializeComponent();
        _agents = new NXData.AgentInstaller.AgentInstaller(_agentOptions);
        _payloadDir = ResolvePayloadDir();
        VersionRun.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?");
        AppPathRun.Text = T("Pasta: ", "Folder: ", "Carpeta: ") + $"{_installDir}  ";
        OnnxPathRun.Text = T("Pasta: ", "Folder: ", "Carpeta: ") + $"{_onnx.Folder}  ";
        LanguageBox.SelectedIndex = IsSpanish ? 2 : IsEnglish ? 1 : 0;
        Loaded += async (_, _) =>
        {
            RefreshAll();
            await TryShotAsync();
        };
    }

    /// <summary>
    /// Captura para documentação: NXPilot-Setup.exe --shot saida.png [--tab 2]. Renderiza só a própria
    /// janela (RenderTargetBitmap) — nunca a tela — e fecha.
    /// </summary>
    private async Task TryShotAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--shot");
        if (i < 0 || i + 1 >= args.Length) return;
        if (args.Contains("--tab") && args.SkipWhile(a => a != "--tab").Skip(1).FirstOrDefault() == "2") Tabs.SelectedItem = AgentsTab; // --tab 2: Agentes e IA
        await Task.Delay(4000); // WSL/versões dos agentes
        var root = (FrameworkElement)VisualTreeHelper.GetChild(this, 0); // área cliente inteira (com as margens)
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(args[i + 1])) encoder.Save(fs);
        Close();
    }

    private void RefreshAll()
    {
        AppPathRun.Text = T("Pasta: ", "Folder: ", "Carpeta: ") + $"{_installDir}  ";
        OnnxPathRun.Text = T("Pasta: ", "Folder: ", "Carpeta: ") + $"{_onnx.Folder}  ";
        VersionLatestItem.Content = T("Última publicada (atualiza sozinha)", "Latest release (auto-updates)", "Última publicada (se actualiza sola)");
        VersionTestedItem.Content = T($"Testada com o NXPilot (Claude {_agentOptions.TestedClaudeVersion} · Codex {_agentOptions.TestedCodexVersion})",
                                      $"Tested with NXPilot (Claude {_agentOptions.TestedClaudeVersion} · Codex {_agentOptions.TestedCodexVersion})",
                                      $"Probada con NXPilot (Claude {_agentOptions.TestedClaudeVersion} · Codex {_agentOptions.TestedCodexVersion})");
        RefreshDotNet(); RefreshStep1(); RefreshStep2(); RefreshAgents(); RefreshLlama(); UpdateAgentCommands();
    }

    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string code }) return;
        App.ApplyLanguage(code);
        RefreshAll();
    }

    private static string ResolvePayloadDir()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--payload");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : AppContext.BaseDirectory;
    }

    /// <summary>A base do app está ao lado do Setup? (onnxruntime.dll é do app; o Setup sozinho não tem.)</summary>
    private bool BasePresent => File.Exists(Path.Combine(_payloadDir, "onnxruntime.dll"));

    /// <summary>Só em desenvolvimento: --payload com o app publicado inteiro instala esse app, sem baixar.</summary>
    private bool LocalAppPresent => File.Exists(Path.Combine(_payloadDir, AppExeName));
    private string InstalledExe => Path.Combine(_installDir, AppExeName);

    // ------------------------------------------------------------------ Passo 1: .NET 10 Desktop Runtime

    // Mesmo instalador oficial do NXProject-Setup (Microsoft, sempre o 10.x mais novo).
    private const string DotNetRuntimeInstallerUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string DotNetDownloadPage = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>.NET Desktop Runtime 10+ instalado na máquina (Program Files\dotnet), como o NXProject confere.</summary>
    private static bool IsDesktopRuntimeInstalled(int minMajor = 10)
    {
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        return Directory.Exists(shared) && Directory.GetDirectories(shared)
            .Any(d => int.TryParse(Path.GetFileName(d).Split('.')[0], out var major) && major >= minMajor);
    }

    private void RefreshDotNet()
    {
        var installed = IsDesktopRuntimeInstalled();
        DotNetStatusText.Text = installed
            ? T("✓ O .NET 10 Desktop Runtime já está instalado nesta máquina.", "✓ .NET 10 Desktop Runtime is already installed on this machine.", "✓ .NET 10 Desktop Runtime ya está instalado en esta máquina.")
            : T("O .NET 10 Desktop Runtime não foi encontrado nesta máquina.", ".NET 10 Desktop Runtime was not found on this machine.", "No se encontró .NET 10 Desktop Runtime en esta máquina.");
        DotNetButton.IsEnabled = !installed;
        DotNetManualButton.IsEnabled = !installed; // alternativa à instalação automática
    }

    /// <summary>Baixa o instalador oficial e instala em silêncio (pede administrador pelo UAC).</summary>
    private async void OnDotNetClick(object sender, RoutedEventArgs e)
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-{Guid.NewGuid():N}.exe");
        try
        {
            DotNetButton.IsEnabled = false;
            DotNetStatusText.Text = T("Baixando o instalador do .NET 10 Desktop Runtime…", "Downloading the .NET 10 Desktop Runtime installer…", "Descargando el instalador de .NET 10 Desktop Runtime…");
            DotNetProgress.Value = 0;
            DotNetProgressPanel.Visibility = Visibility.Visible;
            using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await client.GetAsync(DotNetRuntimeInstallerUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0L;
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(tempExe);
                var buffer = new byte[81920];
                long read = 0; int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    DotNetProgress.Value = total > 0 ? Math.Min(90, read * 90.0 / total) : 45;
                }
            }

            // Instalação silenciosa (UAC): não dá para medir; barra indeterminada.
            DotNetStatusText.Text = T("Instalando o .NET 10 Desktop Runtime (pode pedir permissão de administrador)…", "Installing .NET 10 Desktop Runtime (may ask for administrator permission)…", "Instalando .NET 10 Desktop Runtime (puede pedir permiso de administrador)…");
            DotNetProgress.IsIndeterminate = true;
            using (var proc = Process.Start(new ProcessStartInfo(tempExe, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" }))
                if (proc != null) await proc.WaitForExitAsync();

            RefreshDotNet();
            if (IsDesktopRuntimeInstalled())
                DotNetStatusText.Text = T("✓ .NET 10 Desktop Runtime instalado.", "✓ .NET 10 Desktop Runtime installed.", "✓ .NET 10 Desktop Runtime instalado.");
        }
        catch (Exception ex)
        {
            // Inclui o "não" no UAC (Win32Exception): o usuário pode tentar de novo ou baixar no navegador.
            DotNetStatusText.Text = T($"Não deu para instalar o .NET: {ex.Message}. Use Baixar no navegador ou {DotNetDownloadPage}.",
                                      $"Could not install .NET: {ex.Message}. Use Download in browser or {DotNetDownloadPage}.",
                                      $"No se pudo instalar .NET: {ex.Message}. Use Descargar en el navegador o {DotNetDownloadPage}.");
            DotNetButton.IsEnabled = true;
        }
        finally
        {
            try { File.Delete(tempExe); } catch { /* temporário */ }
            DotNetProgress.IsIndeterminate = false;
            DotNetProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Abre o instalador oficial no navegador, para baixar e instalar manualmente.</summary>
    private void OnDotNetManualClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DotNetRuntimeInstallerUrl) { UseShellExecute = true }); }
        catch (Exception ex) { DotNetStatusText.Text = ex.Message + " — " + DotNetDownloadPage; }
    }

    // ------------------------------------------------------------------ Passo 2: app

    private void RefreshStep1()
    {
        if (!BasePresent)
        {
            Step1StatusText.Text = T($"Base do NXPilot não encontrada em {_payloadDir}. Gere o Setup com release-nxpilot-setup.ps1.", $"NXPilot base not found in {_payloadDir}. Build the Setup with release-nxpilot-setup.ps1.", $"No se encontró la base de NXPilot en {_payloadDir}. Genere el Setup con release-nxpilot-setup.ps1.");
            Step1Button.IsEnabled = false;
        }
        else if (File.Exists(InstalledExe))
        {
            var installed = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion;
            Step1StatusText.Text = T($"NXPilot {installed} instalado. Atualizar para a última versão baixa a mais nova do GitHub (e a base deste Setup).",
                                     $"NXPilot {installed} installed. Update to the latest version downloads the newest one from GitHub (and this Setup's base).",
                                     $"NXPilot {installed} instalado. Actualizar a la última versión descarga la más reciente de GitHub (y la base de este Setup).");
            Step1Button.Content = T("🔄 Atualizar para a última versão", "🔄 Update to the latest version", "🔄 Actualizar a la última versión"); // instalado: você já verificou no app (era "Reinstalar")
            OpenAppButton.Visibility = Visibility.Visible;
            AppRemoveButton.Visibility = Visibility.Visible;
            AppRemoveButton.ToolTip = T("Remover o NXPilot deste computador (pergunta antes; seus dados só saem se você quiser)",
                                        "Remove NXPilot from this computer (asks first; your data is removed only if you want)",
                                        "Quitar NXPilot de esta computadora (pregunta antes; sus datos solo se borran si usted quiere)");
        }
        else
        {
            Step1StatusText.Text = T("Baixa a versão mais nova do NXPilot do GitHub, instala no seu perfil (com o runtime .NET que vem no Setup) e cria um atalho na área de trabalho.",
                                     "Downloads the latest NXPilot from GitHub, installs it in your profile (with the .NET runtime that comes in the Setup) and creates a desktop shortcut.",
                                     "Descarga la versión más reciente de NXPilot de GitHub, la instala en su perfil (con el runtime de .NET que trae el Setup) y crea un acceso directo en el escritorio.");
            Step1Button.Content = T("Instalar", "Install", "Instalar");
            AppRemoveButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Pasta dos dados do NXPilot (tarefas, histórico, configurações das extensões).</summary>
    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot");

    /// <summary>
    /// 🗑 do NXPilot: remove o programa e o atalho; os dados (tarefas, histórico, configurações) saem só se a pessoa
    /// confirmar à parte — é direito dela apagar tudo, e também guardar. Com o NXPilot aberto, pede para fechar.
    /// </summary>
    private async void OnRemoveAppClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)).Length > 0)
        {
            ShowError(T("O NXPilot está aberto: feche-o (e espere terminar a tarefa, se houver) e tente de novo.",
                        "NXPilot is open: close it (and let any task finish) and try again.",
                        "NXPilot está abierto: ciérrelo (y espere a que termine la tarea, si la hay) e intente de nuevo."));
            return;
        }
        var ask = T($"Remover o NXPilot deste computador?\n\nApaga o programa ({_installDir}) e o atalho da área de trabalho. Agentes, ONNX e IA Local ficam (cada um tem a sua lixeira).",
                    $"Remove NXPilot from this computer?\n\nDeletes the program ({_installDir}) and the desktop shortcut. Agents, ONNX and Local AI stay (each has its own trash button).",
                    $"¿Quitar NXPilot de esta computadora?\n\nBorra el programa ({_installDir}) y el acceso directo del escritorio. Los agentes, ONNX e IA Local se quedan (cada uno tiene su papelera).");
        if (MessageBox.Show(this, ask, "NXPilot Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var data = Directory.Exists(DataDir) && MessageBox.Show(this,
            T($"Apagar também os seus dados?\n\n{DataDir}\nTarefas, histórico, configurações das extensões e conexões salvas. Não dá para desfazer.\n\nNão = os dados ficam (reinstalar volta como estava).",
              $"Also delete your data?\n\n{DataDir}\nTasks, history, extension settings and saved connections. This cannot be undone.\n\nNo = the data stays (reinstalling brings it back as it was).",
              $"¿Borrar también sus datos?\n\n{DataDir}\nTareas, historial, configuración de las extensiones y conexiones guardadas. No se puede deshacer.\n\nNo = los datos se quedan (reinstalar los recupera tal como estaban)."),
            "NXPilot Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        AppRemoveButton.IsEnabled = Step1Button.IsEnabled = false;
        string? done = null;
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(_installDir)) Directory.Delete(_installDir, recursive: true);
                var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NXPilot.lnk");
                if (File.Exists(shortcut)) File.Delete(shortcut);
                if (data) Directory.Delete(DataDir, recursive: true);
            });
            done = data
                ? T("NXPilot e seus dados removidos.", "NXPilot and your data removed.", "NXPilot y sus datos quitados.")
                : T("NXPilot removido. Seus dados continuam em ", "NXPilot removed. Your data stays in ", "NXPilot quitado. Sus datos siguen en ") + DataDir;
            OpenAppButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(T("Não foi possível remover tudo: algum arquivo está em uso. Feche o NXPilot e tente de novo. ", "Could not remove everything: some file is in use. Close NXPilot and try again. ", "No se pudo quitar todo: algún archivo está en uso. Cierre NXPilot e intente de nuevo. ") + ex.Message);
        }
        finally
        {
            AppRemoveButton.IsEnabled = Step1Button.IsEnabled = true;
            RefreshStep1();
            if (done != null) Step1StatusText.Text = done; // depois do Refresh, que reescreve o texto
        }
    }

    private async void OnStep1Click(object sender, RoutedEventArgs e)
    {
        Step1Button.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        Step1Progress.Value = 0;
        string? package = null;
        try
        {
            // 1. O app vem do GitHub ANTES de mexer na pasta: sem internet, nada fica pela metade.
            var versionNote = "";
            if (!LocalAppPresent)
            {
                var latest = await DownloadLatestAppAsync();
                package = latest.Package;
                versionNote = T($" Versão {latest.Tag}.", $" Version {latest.Tag}.", $" Versión {latest.Tag}.");
            }

            Step1StatusText.Text = T("Encerrando o NXPilot, se estiver aberto…", "Closing NXPilot, if it is open…", "Cerrando NXPilot, si está abierto…");
            await Task.Run(StopRunningApp);

            // 2. A base (runtime + bibliotecas) que vem ao lado do Setup.
            Step1StatusText.Text = T("Copiando arquivos…", "Copying files…", "Copiando archivos…");
            var progress = new Progress<double>(p => Step1Progress.Value = 20 + p * 60);
            await Task.Run(() => CopyPayload(_payloadDir, _installDir, progress));
            // Exe de versões antigas (antes de se chamar NxPilot.exe). ATENÇÃO: o Windows ignora
            // maiúsculas — nunca pôr aqui "NXPilot.exe", que apagaria o NxPilot.exe recém-copiado.
            foreach (var legacy in new[] { "NXAgentic.exe", "NXAgentic.dll", "NXAgentic.deps.json", "NXAgentic.runtimeconfig.json", "NXAgentic.pdb" })
                try { File.Delete(Path.Combine(_installDir, legacy)); } catch { /* em uso: some na próxima atualização */ }
            RemoveLegacyInstall();

            // 3. O app por cima da base.
            if (package != null)
            {
                Step1StatusText.Text = T("Instalando o NXPilot…", "Installing NXPilot…", "Instalando NXPilot…");
                await Task.Run(() => NXPilot.Updates.UpdateService.Apply(package, _installDir));
                NXPilot.Updates.UpdateService.CleanupOldFiles(_installDir); // o app está fechado: dá para apagar já
            }
            Step1Progress.Value = 90;

            // Sem o runtime ao lado do exe, o Windows mostra o diálogo genérico "instale o
            // .NET" — erro confuso. Falha aqui, apontando o que faltou.
            var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(_installDir, f))).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(T("instalação incompleta, faltam: ", "incomplete installation, missing: ", "instalación incompleta, faltan: ") + string.Join(", ", missing));

            var shortcut = DesktopShortcut.Create(InstalledExe, "NXPilot");
            Step1Progress.Value = 100;
            RefreshStep1();
            // O fim fica explícito no texto também (não só na barra): "✓ 100% · …".
            Step1StatusText.Text = "✓ 100% · " + T("concluído.", "done.", "concluido.") + versionNote + (shortcut != null
                ? T(" Atalho criado na área de trabalho.", " Desktop shortcut created.", " Acceso directo creado en el escritorio.")
                : T(" Não deu para criar o atalho na área de trabalho: abra pelo botão Abrir NXPilot ou pela pasta do app.",
                    " Could not create the desktop shortcut: use the Open NXPilot button or the app folder.",
                    " No se pudo crear el acceso directo en el escritorio: use el botón Abrir NXPilot o la carpeta de la aplicación."));
        }
        catch (Exception ex)
        {
            ShowError(T("Falha ao instalar: ", "Installation failed: ", "Error al instalar: ") + ex.Message);
            Step1StatusText.Text = T("Instalação interrompida.", "Installation interrupted.", "Instalación interrumpida.");
        }
        finally
        {
            if (package != null) try { Directory.Delete(Path.GetDirectoryName(package)!, true); } catch { /* temporário */ }
            Step1Button.IsEnabled = BasePresent;
        }
    }

    /// <summary>Baixa e confere (SHA-256) o NXPilot-Release.zip da última versão; devolve a pasta extraída.</summary>
    private async Task<(string Tag, string Package)> DownloadLatestAppAsync()
    {
        try
        {
            Step1StatusText.Text = T("Procurando a versão mais nova…", "Looking for the latest version…", "Buscando la versión más reciente…");
            var updates = new NXPilot.Updates.UpdateService();
            var latest = (await updates.ListVersionsAsync(1)).FirstOrDefault()
                         ?? throw new NXPilot.Updates.UpdateException(T("nenhuma versão publicada.", "no version published.", "ninguna versión publicada."));
            Step1StatusText.Text = T($"Baixando o NXPilot {latest.Tag}…", $"Downloading NXPilot {latest.Tag}…", $"Descargando NXPilot {latest.Tag}…");
            Step1Progress.Value = 5;
            // Download do app: 5% → 20% conforme os bytes chegam (MB e % no texto).
            var package = await updates.DownloadAsync(latest, null, default, new Progress<(long Downloaded, long Total)>(b =>
            {
                if (b.Total > 0) Step1Progress.Value = 5 + b.Downloaded * 15d / b.Total;
                Step1StatusText.Text = T($"Baixando o NXPilot {latest.Tag}… ", $"Downloading NXPilot {latest.Tag}… ", $"Descargando NXPilot {latest.Tag}… ")
                                       + NXPilot.Updates.UpdateService.DescribeProgress(b.Downloaded, b.Total);
            }));
            Step1Progress.Value = 20;
            return (latest.Tag, package);
        }
        catch (Exception ex) when (ex is NXPilot.Updates.UpdateException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(T(
                "não deu para baixar o NXPilot do GitHub. O Setup traz só a base (runtime e bibliotecas); o app vem da internet. Confira a conexão e tente de novo. Detalhe: ",
                "could not download NXPilot from GitHub. The Setup only carries the base (runtime and libraries); the app comes from the internet. Check your connection and try again. Detail: ",
                "no se pudo descargar NXPilot de GitHub. El Setup solo trae la base (runtime y bibliotecas); la aplicación viene de internet. Revise la conexión e intente de nuevo. Detalle: ") + ex.Message, ex);
        }
    }

    private static readonly string[] RequiredFiles =
    {
        AppExeName, "NxPilot.dll", "NXPilot.Core.dll", "NxPilot.runtimeconfig.json",
        "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "PresentationFramework.dll", "onnxruntime.dll"
    };

    // Arquivos do próprio Setup: ficam fora da pasta do app.
    private static readonly string[] SetupOwnFiles =
    {
        "NXPilot-Setup.exe", "NXPilot-Setup.dll", "NXPilot-Setup.pdb",
        "NXPilot-Setup.deps.json", "NXPilot-Setup.runtimeconfig.json"
    };

    private static void CopyPayload(string source, string dest, IProgress<double> progress)
    {
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => !SetupOwnFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, files[i]));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[i], target, overwrite: true);
            if (i % 20 == 0) progress.Report((double)i / files.Count);
        }
        progress.Report(1);
    }

    /// <summary>O app aberto segura DLLs e a cópia falha com "being used by another process".</summary>
    private static void StopRunningApp()
    {
        var procs = Process.GetProcessesByName("NxPilot").Concat(Process.GetProcessesByName("NXPilot")).ToArray();
        foreach (var p in procs)
        {
            try { p.Kill(true); p.WaitForExit(5000); }
            catch { /* já saiu ou sem permissão: tenta copiar mesmo assim */ }
            finally { p.Dispose(); }
        }
        if (procs.Length > 0) Thread.Sleep(500); // o SO libera os handles logo depois
    }

    /// <summary>
    /// O produto público se chamava NXAgentic (pasta %USERPROFILE%\Apps\NXAgentic e atalho
    /// NXAgentic.lnk). Remove a instalação antiga — só se for MESMO nossa (tem o NxPilot.exe) —
    /// para não ficarem dois apps. Os dados do usuário migram no primeiro início do app.
    /// </summary>
    private static void RemoveLegacyInstall()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "NXAgentic");
        try { if (File.Exists(Path.Combine(old, "NxPilot.exe"))) Directory.Delete(old, recursive: true); }
        catch { /* em uso: fica para a próxima atualização */ }
        try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NXAgentic.lnk")); }
        catch { /* sem atalho antigo */ }
    }

    // ------------------------------------------------------------------ Passo 3: ONNX

    private void RefreshStep2()
    {
        var ok = _onnx.IsInstalled;
        Step2StatusText.Text = ok ? T("Modelo instalado.", "Model installed.", "Modelo instalado.") : T("Modelo ainda não baixado.", "Model not downloaded yet.", "Modelo aún no descargado.");
        Step2Button.Content = ok ? T("Já instalado", "Already installed", "Ya instalado") : T("Baixar modelo", "Download model", "Descargar modelo");
        Step2Button.IsEnabled = !ok;
        OnnxRemoveButton.Visibility = File.Exists(_onnx.ModelPath) || File.Exists(_onnx.VocabPath) ? Visibility.Visible : Visibility.Collapsed;
        OnnxRemoveButton.ToolTip = T("Remover o classificador ONNX (pergunta antes)", "Remove the ONNX classifier (asks first)", "Quitar el clasificador ONNX (pregunta antes)");
    }

    /// <summary>🗑 do classificador ONNX: pergunta e remove o modelo (o gate fica só com as regras).</summary>
    private void OnRemoveOnnxClick(object sender, RoutedEventArgs e)
    {
        var ask = T($"Remover o classificador ONNX ({_onnx.Folder})?\n\nSem ele, o NXPilot classifica as ações só pelas regras — mais pedidos de aprovação chegam até você.",
                    $"Remove the ONNX classifier ({_onnx.Folder})?\n\nWithout it, NXPilot classifies actions with rules only — more approval requests reach you.",
                    $"¿Quitar el clasificador ONNX ({_onnx.Folder})?\n\nSin él, NXPilot clasifica las acciones solo con reglas — le llegan más solicitudes de aprobación.");
        if (MessageBox.Show(this, ask, "NXPilot Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            _onnx.Uninstall();
            Step2ProgressText.Text = T("Classificador ONNX removido.", "ONNX classifier removed.", "Clasificador ONNX quitado.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
        RefreshStep2();
    }

    private async void OnStep2Click(object sender, RoutedEventArgs e)
    {
        Step2Button.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _onnx.DownloadAsync(new Progress<string>(m => Step2ProgressText.Text = m));
        }
        catch (UnauthorizedAccessException)
        {
            ShowError(T($"Sem permissão para gravar em {_onnx.Folder}. Crie a pasta C:\\IA_LOCAL manualmente e tente de novo.", $"No permission to write to {_onnx.Folder}. Create the C:\\IA_LOCAL folder manually and try again.", $"Sin permiso para escribir en {_onnx.Folder}. Cree la carpeta C:\\IA_LOCAL manualmente e intente de nuevo."));
        }
        catch (Exception ex) { ShowError(T("Falha no download do modelo: ", "Model download failed: ", "Error al descargar el modelo: ") + ex.Message); }
        finally { RefreshStep2(); }
    }

    // ------------------------------------------------------------------ Passo 4: agentes

    private enum Where { None, Windows, Wsl }

    /// <summary>
    /// NXData.AgentInstaller.AgentLocator: PATH, pastas dos instaladores oficiais, pasta do NXProject
    /// e extensões do VS Code — quem já tem o agente (de qualquer forma) não reinstala.
    /// </summary>
    private void RefreshAgents()
    {
        foreach (var (cli, status, name, button) in new[] { (NXData.AgentInstaller.AgentCli.ClaudeCode, ClaudeStatus, "claude", ClaudeInstallButton), (NXData.AgentInstaller.AgentCli.Codex, CodexStatus, "codex", CodexInstallButton) })
        {
            // 🗑: a regra é do AgentPanel (a mesma do NXPilot → Configurar → Agentes de código).
            var remove = cli == NXData.AgentInstaller.AgentCli.ClaudeCode ? ClaudeRemoveButton : CodexRemoveButton;
            var removeState = NXData.AgentInstaller.AgentPanel.Remove(_agents, _agentOptions, cli, T);
            remove.Visibility = removeState.Visible ? Visibility.Visible : Visibility.Collapsed;
            remove.IsEnabled = removeState.Enabled;
            remove.ToolTip = removeState.Tip;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(remove, true);
            var check = ++_agentCheck[(int)cli]; // só a conferência mais recente mexe no botão
            button.SetResourceReference(ContentProperty, "Setup_Instalar");
            button.IsEnabled = true;
            if (NXData.AgentInstaller.AgentLocator.Find(cli, _agentOptions) is { } found)
            {
                status.Text = NXData.AgentInstaller.AgentPanel.InstalledText(found, T);
                status.Foreground = Brushes.Green;
                if (found.Version != null) _ = CheckAgentUpdateAsync(cli, check, found.Version, status, button);
            }
            else _ = CheckWslAsync(name, status);
        }
    }

    private readonly int[] _agentCheck = new int[2];

    /// <summary>Instalar/atualizar deu erro: o botão continua habilitado para tentar de novo.</summary>
    private readonly HashSet<NXData.AgentInstaller.AgentCli> _agentFailed = new();

    /// <summary>
    /// Compara a instalada com a versão escolhida (testada ou última): mais nova disponível =
    /// "Atualizar" habilitado; já atualizado = "Atualizado" desabilitado (salvo se a última
    /// tentativa deu erro). Sem internet, o botão fica habilitado (não dá para comparar).
    /// </summary>
    private async Task CheckAgentUpdateAsync(NXData.AgentInstaller.AgentCli cli, int check, string installed, TextBlock status, Button button)
    {
        try
        {
            var (target, _) = await _agents.ResolveVersionAsync(cli, SelectedVersion);
            if (check != _agentCheck[(int)cli]) return;
            // Instalar / Atualizar / ✓ Atualizado: regra do AgentPanel (a mesma do app).
            var (kind, enabled) = NXData.AgentInstaller.AgentPanel.Install(new NXData.AgentInstaller.AgentLocation(cli, "", "", installed), target, _agentFailed.Contains(cli));
            button.Content = NXData.AgentInstaller.AgentPanel.InstallLabel(kind, T);
            button.IsEnabled = enabled;
            if (kind == NXData.AgentInstaller.AgentPanel.InstallButton.Update)
            {
                status.Text += NXData.AgentInstaller.AgentPanel.AvailableSuffix(target, T);
                status.Foreground = Brushes.DarkOrange;
            }
        }
        catch { /* sem internet ou serviço fora: não dá para comparar */ }
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e) => RefreshAgents();

    private NXData.AgentInstaller.InstallMode SelectedMode => ModeScript.IsChecked == true ? NXData.AgentInstaller.InstallMode.Script : NXData.AgentInstaller.InstallMode.Internal;
    private NXData.AgentInstaller.VersionChoice SelectedVersion => VersionBox.SelectedItem == VersionTestedItem ? NXData.AgentInstaller.VersionChoice.Tested : NXData.AgentInstaller.VersionChoice.Latest;

    private void OnInstallOptionChanged(object sender, RoutedEventArgs e)
    {
        UpdateAgentCommands();
        // Testada x última muda a versão alvo: o botão Instalar/Atualizar acompanha.
        if (sender == VersionBox && IsLoaded) RefreshAgents();
    }

    /// <summary>Comando oficial equivalente à escolha (para ver/copiar; é o que o modo Script roda).</summary>
    private void UpdateAgentCommands()
    {
        if (ClaudeCommand == null || VersionBox == null) return; // eventos durante o InitializeComponent
        var tested = SelectedVersion == NXData.AgentInstaller.VersionChoice.Tested;
        ClaudeCommand.Text = _agents.ScriptCommand(NXData.AgentInstaller.AgentCli.ClaudeCode, tested ? _agentOptions.TestedClaudeVersion : null);
        CodexCommand.Text = _agents.ScriptCommand(NXData.AgentInstaller.AgentCli.Codex, tested ? _agentOptions.TestedCodexVersion : null);
    }

    private async void OnInstallAgentClick(object sender, RoutedEventArgs e)
    {
        var cli = (sender as Button)?.Tag as string == "claude" ? NXData.AgentInstaller.AgentCli.ClaudeCode : NXData.AgentInstaller.AgentCli.Codex;
        ClaudeInstallButton.IsEnabled = CodexInstallButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var r = await _agents.InstallAsync(cli, SelectedMode, SelectedVersion, new Progress<string>(m => AgentProgress.Text = m));
            _agentFailed.Remove(cli);
            AgentProgress.Text = r.Message + (r.FellBackToLatest ? "" : T(" Sem login, o NXPilot abre o login no navegador na primeira tarefa.", " If not signed in, NXPilot opens the sign-in page in the browser on the first task.", " Sin inicio de sesión, NXPilot abre el inicio de sesión en el navegador en la primera tarea."));
        }
        catch (Exception ex)
        {
            _agentFailed.Add(cli);
            AgentProgress.Text = "";
            ShowError((cli == NXData.AgentInstaller.AgentCli.ClaudeCode ? "Claude Code: " : "Codex: ") + ex.Message +
                      (SelectedMode == NXData.AgentInstaller.InstallMode.Script ? T(" Tente a instalação Automática.", " Try the Automatic install.", " Pruebe la instalación Automática.") : ""));
        }
        finally { RefreshAgents(); } // decide habilitado/desabilitado de cada botão
    }

    /// <summary>🗑 ao lado do Instalar: pergunta e remove o agente (o login da pessoa fica).</summary>
    private async void OnRemoveAgentClick(object sender, RoutedEventArgs e)
    {
        var cli = (sender as Button)?.Tag as string == "claude" ? NXData.AgentInstaller.AgentCli.ClaudeCode : NXData.AgentInstaller.AgentCli.Codex;
        var ask = NXData.AgentInstaller.AgentPanel.RemoveQuestion(cli, T);
        if (MessageBox.Show(this, ask, "NXPilot Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ClaudeInstallButton.IsEnabled = CodexInstallButton.IsEnabled = ClaudeRemoveButton.IsEnabled = CodexRemoveButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try { AgentProgress.Text = await _agents.UninstallAsync(cli); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            ClaudeRemoveButton.IsEnabled = CodexRemoveButton.IsEnabled = true;
            RefreshAgents();
        }
    }

    private async Task CheckWslAsync(string cli, TextBlock status)
    {
        status.Text = T("verificando WSL…", "checking WSL…", "verificando WSL…");
        status.Foreground = Brushes.Gray;
        var found = await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("wsl.exe", $"-- bash -lic \"command -v {cli} >/dev/null 2>&1 && echo FOUND\"")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return output.Contains("FOUND", StringComparison.Ordinal);
            }
            catch { return false; }
        });
        SetAgentStatus(status, found ? Where.Wsl : Where.None);
    }

    private static void SetAgentStatus(TextBlock tb, Where where)
    {
        tb.Text = where switch
        {
            Where.Windows => T("✓ instalado (Windows)", "✓ installed (Windows)", "✓ instalado (Windows)"),
            Where.Wsl => T("✓ instalado (WSL)", "✓ installed (WSL)", "✓ instalado (WSL)"),
            _ => T("não encontrado", "not found", "no encontrado")
        };
        tb.Foreground = where == Where.None ? Brushes.Gray : Brushes.Green;
    }

    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        var text = (sender as Button)?.Tag as string == "claude" ? ClaudeCommand.Text : CodexCommand.Text;
        try { Clipboard.SetText(text); } catch { /* área de transferência ocupada por outro app */ }
    }

    // ------------------------------------------------------------------ Passo 5: LLaMA

    private readonly NXData.AgentInstaller.LocalAiInstaller _llama = new();
    private CancellationTokenSource? _llamaCts;

    /// <summary>
    /// Mesma pasta e checagem do NXProject (local-ai.json dele, ou C:\IA_LOCAL): backend + .gguf
    /// válido. Sem a IA Local, aparece o Instalar (o NXData.AgentInstaller baixa backend e modelo).
    /// </summary>
    private void RefreshLlama()
    {
        var status = _llama.Check();
        var ok = status.Installed;
        LlamaStatus.Text = ok ? T("✓ instalada", "✓ installed", "✓ instalada") : T("não instalada", "not installed", "no instalada");
        LlamaStatus.Foreground = ok ? Brushes.Green : Brushes.Gray;
        LlamaPathText.Text = T("Pasta: ", "Folder: ", "Carpeta: ") + status.Folder + (status.ModelFile is { } m && ok ? $"  ·  {m}" : "");
        // Como o passo do ONNX: o botão fica sempre, desabilitado como "Já instalada".
        if (_llamaCts != null) return; // instalando: quem cuida do botão é o OnInstallLlamaClick
        LlamaInstallButton.Content = ok ? T("Já instalada", "Already installed", "Ya instalada") : T("Instalar IA Local (~2,5 GB)", "Install Local AI (~2.5 GB)", "Instalar IA Local (~2,5 GB)");
        LlamaInstallButton.IsEnabled = !ok;
        LlamaRemoveButton.Visibility = status.BackendPresent || status.ModelPresent ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>🗑 da IA Local: pergunta e remove modelo e backend (a pasta é a mesma do NXProject).</summary>
    private async void OnRemoveLlamaClick(object sender, RoutedEventArgs e)
    {
        var folder = _llama.ResolveFolder();
        var ask = T($"Remover a IA Local ({folder})?\n\nApaga o modelo (~2,5 GB) e o backend. O NXProject usa esta mesma IA Local e também deixa de tê-la. O gate do NXPilot continua com Regras e ONNX.",
                    $"Remove the Local AI ({folder})?\n\nDeletes the model (~2.5 GB) and the backend. NXProject uses this same Local AI and will lose it too. The NXPilot gate keeps running with Rules and ONNX.",
                    $"¿Quitar la IA Local ({folder})?\n\nBorra el modelo (~2,5 GB) y el backend. NXProject usa esta misma IA Local y también la pierde. El gate de NXPilot sigue con Reglas y ONNX.");
        if (MessageBox.Show(this, ask, "NXPilot Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ErrorText.Visibility = Visibility.Collapsed;
        LlamaRemoveButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _llama.Uninstall(folder));
            LlamaProgressText.Text = T("IA Local removida.", "Local AI removed.", "IA Local quitada.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { LlamaRemoveButton.IsEnabled = true; RefreshLlama(); }
    }

    private async void OnInstallLlamaClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        _llamaCts = new CancellationTokenSource();
        LlamaInstallButton.IsEnabled = false;
        LlamaCancelButton.Visibility = LlamaProgressPanel.Visibility = Visibility.Visible;
        LlamaProgress.Value = 0;
        try
        {
            var progress = new Progress<NXData.AgentInstaller.LocalAiProgress>(p =>
            {
                LlamaProgressText.Text = p.Message;
                LlamaProgress.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) LlamaProgress.Value = p.Percent;
            });
            var result = await _llama.InstallAsync(progress: progress, ct: _llamaCts.Token);
            LlamaProgressText.Text = result.Installed
                ? T("IA Local instalada. O NXPilot e o NXProject usam esta mesma pasta.", "Local AI installed. NXPilot and NXProject share this folder.", "IA Local instalada. NXPilot y NXProject usan esta misma carpeta.")
                : T("A instalação não terminou: tente de novo.", "The installation did not finish: try again.", "La instalación no terminó: intente de nuevo.");
        }
        catch (OperationCanceledException) { LlamaProgressText.Text = T("Cancelado. Dá para instalar depois, aqui ou pelo NXProject.", "Cancelled. You can install it later, here or from NXProject.", "Cancelado. Puede instalarla después, aquí o desde NXProject."); }
        catch (UnauthorizedAccessException)
        {
            ShowError(T($"Sem permissão para gravar em {_llama.ResolveFolder()}. Crie a pasta manualmente e tente de novo.", $"No permission to write to {_llama.ResolveFolder()}. Create the folder manually and try again.", $"Sin permiso para escribir en {_llama.ResolveFolder()}. Cree la carpeta manualmente e intente de nuevo."));
        }
        catch (Exception ex) { ShowError(T("Falha ao instalar a IA Local: ", "Local AI installation failed: ", "Error al instalar la IA Local: ") + ex.Message); }
        finally
        {
            _llamaCts.Dispose();
            _llamaCts = null;
            LlamaCancelButton.Visibility = LlamaProgressPanel.Visibility = Visibility.Collapsed;
            RefreshLlama();
        }
    }

    private void OnCancelLlamaClick(object sender, RoutedEventArgs e) => _llamaCts?.Cancel();

    // ------------------------------------------------------------------ comum

    private void OnOpenAppClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledExe) { WorkingDirectory = _installDir, UseShellExecute = true });
            Close();
        }
        catch (Exception ex) { ShowError(T("Não foi possível abrir o NXPilot: ", "Could not open NXPilot: ", "No se pudo abrir NXPilot: ") + ex.Message); }
    }

    private void OnOpenAppFolder(object sender, RoutedEventArgs e) => OpenFolder(_installDir);
    private void OnOpenOnnxFolder(object sender, RoutedEventArgs e) => OpenFolder(_onnx.Folder);
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* conveniência */ }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
