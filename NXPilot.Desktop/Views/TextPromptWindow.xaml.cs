// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>Pede um texto livre; <see cref="Result"/> nulo = cancelou.</summary>
public partial class TextPromptWindow : Window
{
    public string? Result { get; private set; }

    public TextPromptWindow(string title, string message, string okText, string initial = "")
    {
        InitializeComponent();
        Title = Heading.Text = title;
        Message.Text = message;
        InputBox.Text = initial;
        OkButton.Content = okText;
        CancelButton.Content = T("Cancelar", "Cancel", "Cancelar");
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
    }

    /// <summary>Atalho: abre e devolve o texto (nulo se cancelou).</summary>
    public static string? Ask(Window owner, string title, string message, string okText, string initial = "")
    {
        var w = new TextPromptWindow(title, message, okText, initial) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }
}
