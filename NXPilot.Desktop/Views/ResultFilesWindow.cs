// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NXPilot.Agents;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Arquivos que uma tarefa do Histórico criou ou alterou (entregáveis primeiro), para ver o resultado SEM trocar a
/// tarefa da tela — funciona com outra tarefa rodando. Só leitura: abre documentos no programa padrão (nunca script).
/// </summary>
public sealed class ResultFilesWindow : Window
{
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public ResultFilesWindow(TaskRecord record)
    {
        Title = T("Arquivos gerados — ", "Generated files — ", "Archivos generados — ") + record.Info.Title;
        Width = 640; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var open = new Button { Content = T("Abrir", "Open", "Abrir"), Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        var folder = new Button { Content = T("Abrir pasta", "Open folder", "Abrir carpeta"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = T("Fechar", "Close", "Cerrar"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(open); buttons.Children.Add(folder); buttons.Children.Add(close);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(_list);
        Content = root;

        open.Click += (_, _) => OpenSelected();
        _list.MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left) OpenSelected(); };
        folder.Click += (_, _) =>
        {
            var target = (_list.SelectedItem as ListBoxItem)?.Tag is ResultFile f ? Path.GetDirectoryName(f.FullPath) : record.Worktree;
            if (target != null && Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        };
        Loaded += async (_, _) => await LoadAsync(record);
    }

    private async Task LoadAsync(TaskRecord r)
    {
        if (r.Worktree is not { } wt || !Directory.Exists(wt))
        {
            _status.Text = T("O worktree desta tarefa não existe mais.", "This task's worktree no longer exists.", "El worktree de esta tarea ya no existe.");
            return;
        }
        try
        {
            var files = (await GitWorktree.ChangedFilesAsync(wt, r.BaseCommit))
                .Select(rel => new ResultFile(rel, Path.Combine(wt, rel.Replace('/', Path.DirectorySeparatorChar))))
                .OrderByDescending(f => f.IsDeliverable).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in files)
                _list.Items.Add(new ListBoxItem
                {
                    Tag = f, Padding = new Thickness(4, 3, 4, 3),
                    Content = $"{f.Glyph}  {f.RelativePath}",
                    FontWeight = f.IsDeliverable ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = f.CanOpen ? Brushes.Black : Brushes.Gray,
                    ToolTip = f.CanOpen ? f.FullPath : T("Não é documento: só pela pasta (abrir um script o executaria).", "Not a document: only via the folder (opening a script would run it).", "No es documento: solo por la carpeta (abrir un script lo ejecutaría)."),
                });
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _status.Text = files.Count == 0
                ? T("A tarefa ainda não criou nem alterou arquivos.", "The task has not created or changed files yet.", "La tarea aún no creó ni modificó archivos.")
                : T($"{files.Count} arquivo(s). Duplo clique abre o documento no programa padrão.", $"{files.Count} file(s). Double-click opens the document in the default program.", $"{files.Count} archivo(s). Doble clic abre el documento en el programa predeterminado.");
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void OpenSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not ResultFile { CanOpen: true } f || !File.Exists(f.FullPath)) return;
        try { Process.Start(new ProcessStartInfo(f.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
