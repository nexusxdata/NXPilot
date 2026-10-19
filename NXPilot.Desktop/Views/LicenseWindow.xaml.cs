// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Windows;
using NXPilot.Desktop.Services;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Licença de uso (resumo + link do texto oficial). Dois modos, como no NXProject:
/// primeira execução (aceitar ou sair; o X conta como sair) e consulta (selo "Termo aceito em …" e Fechar).
/// </summary>
public partial class LicenseWindow : Window
{
    /// <summary>true = primeira execução: sem aceite o app não abre.</summary>
    public bool RequireAcceptance { get; init; }

    // Texto oficial em português; EN e ES apontam para a tradução completa em inglês.
    private static string FullTextUrl => IsEnglish || IsSpanish
        ? "https://github.com/nexusxdata/NXPilot/blob/main/LICENSE.en.txt"
        : "https://github.com/nexusxdata/NXPilot/blob/main/LICENSE.txt";

    public LicenseWindow()
    {
        InitializeComponent();
        FullTextUrlRun.Text = FullTextUrl;
        Loaded += (_, _) => ApplyMode();
    }

    private void ApplyMode()
    {
        if (RequireAcceptance) return;
        AcceptNote.Visibility = Visibility.Collapsed;
        DeclineButton.Visibility = Visibility.Collapsed;
        AcceptButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.IsDefault = true;

        AcceptedSeal.Text = LicenseAcceptanceStore.AcceptedOn() is { } when
            ? T($"✔ Termo aceito em {when:dd/MM/yyyy}", $"✔ Terms accepted on {when:yyyy-MM-dd}", $"✔ Términos aceptados el {when:dd/MM/yyyy}")
            : T("✔ Termo aceito nesta máquina", "✔ Terms accepted on this machine", "✔ Términos aceptados en esta máquina");
        AcceptedSeal.Visibility = LicenseAcceptanceStore.HasAccepted() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDeclineClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnFullTextClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(FullTextUrl) { UseShellExecute = true }); }
        catch { /* sem navegador o link só não abre; o LICENSE.txt também está no repositório */ }
    }
}
