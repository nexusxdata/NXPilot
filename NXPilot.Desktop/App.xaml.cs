// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using NXPilot.Desktop.Services;

namespace NXPilot.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        NXPilot.LegacyDataMigration.Run(); // dados do antigo NXAgentic; antes de ler settings.json (o idioma salvo mora lá)
        NXPilot.Updates.UpdateService.CleanupOldFiles(AppContext.BaseDirectory); // *.nxold da atualização anterior
        // Antes de qualquer janela: os textos já nascem no idioma salvo (ou o do Windows).
        // --lang en-US força o idioma só nesta execução (prints, testes) sem gravar a escolha.
        var i = Array.IndexOf(e.Args, "--lang");
        LanguageService.Apply(i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : LanguageService.LoadPreferred());
        NXPilot.Agents.ShadowRepo.Root = AppSettings.Load().SubjectsRoot ?? ""; // vazio = padrão
        DispatcherUnhandledException += OnUnhandledException;
        if (!EnsureLicenseAccepted(e.Args)) { Shutdown(); return; }
        base.OnStartup(e);
    }

    /// <summary>
    /// Primeira execução: a licença aparece antes da janela principal e o app só abre com o
    /// "Aceitar e continuar" (como no NXProject). Prints e testes (--shot/--exit) não perguntam.
    /// </summary>
    private bool EnsureLicenseAccepted(string[] args)
    {
        if (LicenseAcceptanceStore.HasAccepted() || args.Contains("--shot") || args.Contains("--exit")) return true;
        // Sem isto, fechar o diálogo (a única janela até aqui) encerraria o app antes da principal.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var accepted = new Views.LicenseWindow { RequireAcceptance = true, WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog() == true;
        if (!accepted) return false;
        LicenseAcceptanceStore.Persist(NXPilot.Updates.UpdateService.CurrentVersion.ToString());
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        return true;
    }

    /// <summary>
    /// Rede de segurança: biblioteca da base faltando (app atualizado sem o Setup novo) não
    /// derruba o NXPilot — avisa e manda para o Setup. Outros erros seguem o caminho normal.
    /// </summary>
    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        for (var ex = e.Exception; ex != null; ex = ex.InnerException)
        {
            if (ex is not (System.IO.FileNotFoundException or System.IO.FileLoadException or DllNotFoundException or BadImageFormatException)) continue;
            e.Handled = true;
            var missing = ex is System.IO.FileNotFoundException f && f.FileName is { } name ? name.Split(',')[0] : ex.Message;
            var ask = NXPilot.NxText.T(
                $"Falta uma biblioteca da base do NXPilot ({missing}). Isso acontece quando o app foi atualizado sem o NXPilot-Setup novo.\n\nAbrir a página do Setup para baixar? Rode o NXPilot-Setup.exe e ele completa a instalação.",
                $"A library from the NXPilot base is missing ({missing}). This happens when the app was updated without the new NXPilot-Setup.\n\nOpen the Setup page to download it? Run NXPilot-Setup.exe and it completes the installation.",
                $"Falta una biblioteca de la base de NXPilot ({missing}). Esto ocurre cuando la app se actualizó sin el NXPilot-Setup nuevo.\n\n¿Abrir la página del Setup para descargarlo? Ejecute NXPilot-Setup.exe y completará la instalación.");
            if (MessageBox.Show(ask, "NXPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"https://github.com/nexusxdata/NXPilot/releases/tag/{NXPilot.Updates.UpdateService.SetupTag}") { UseShellExecute = true });
            return;
        }
    }
}
