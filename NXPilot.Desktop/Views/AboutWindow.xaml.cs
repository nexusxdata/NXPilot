// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Windows;
using NXPilot.Desktop.Services;
using NXPilot.Updates;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>Sobre o NXPilot: logo da Nexus XData, versão instalada, licença, GitHub e contato (como no NXProject).</summary>
public partial class AboutWindow : Window
{
    private const string ContactEmail = "comercial.nexus.xdata@gmail.com";
    private const string GitHubUrl = "https://github.com/nexusxdata/NXPilot";
    private const string NXProjectUrl = "https://github.com/nexusxdata/NXProject";

    public AboutWindow()
    {
        InitializeComponent();
        try { CompanyLogoImage.Source = ProtectedLogoProvider.GetLogoImage(); }
        catch { CompanyLogoImage.Visibility = Visibility.Collapsed; } // sem logo, o resto da tela continua útil

        var v = UpdateService.CurrentVersion;
        VersionText.Text = T($"Versão {v.ToString(3)}.{v.Revision:00}", $"Version {v.ToString(3)}.{v.Revision:00}", $"Versión {v.ToString(3)}.{v.Revision:00}");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnLicenseClick(object sender, RoutedEventArgs e) => new LicenseWindow { Owner = this }.ShowDialog();

    private void OnGitHubClick(object sender, RoutedEventArgs e) => Open(GitHubUrl);

    private void OnNXProjectClick(object sender, RoutedEventArgs e) => Open(NXProjectUrl);

    private void OnEmailClick(object sender, RoutedEventArgs e) => Open($"mailto:{ContactEmail}");

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, T($"Não foi possível abrir {target}.\n\n{ex.Message}", $"Could not open {target}.\n\n{ex.Message}", $"No se pudo abrir {target}.\n\n{ex.Message}"),
                "NXPilot", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
