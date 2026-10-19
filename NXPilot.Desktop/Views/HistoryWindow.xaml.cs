// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.IO;
using System.Windows;
using NXPilot.Agents;
using NXPilot.Desktop.Services;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Histórico de tarefas. Abrir mostra a tarefa na tela (somente leitura); remover apaga o
/// worktree, o branch nxpilot/&lt;id&gt; e o registro — com confirmação, porque não volta.
/// </summary>
public partial class HistoryWindow : Window
{
    private sealed record Row(TaskRecord Record, string When, string Title, string State, string Findings, string Cost, string Repo);

    private readonly bool _taskRunning;

    /// <summary>Tarefa escolhida para abrir na tela principal; nula = só fechou.</summary>
    public TaskRecord? ToOpen { get; private set; }

    public HistoryWindow(bool taskRunning)
    {
        InitializeComponent();
        _taskRunning = taskRunning;
        Reload();
    }

    private void Reload()
    {
        var rows = TaskStore.LoadAll().Select(r =>
        {
            var label = TaskSession.FromRecord(r).StateLabel;
            var findings = r.Verification is { } v
                ? T($"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crit. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total")
                : "—";
            return new Row(r, r.CreatedAt.ToString("dd/MM HH:mm"), r.Info.Title, label, findings,
                r.Tokens is { Total: > 0 } t ? NXPilot.Agents.TokenUsage.Format(t.Total) : "—", r.Info.Subject is { } subject ? "◆ " + subject : r.Info.RepoPath);
        }).ToList();
        Grid.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) Grid.SelectedIndex = 0;
        OnSelectionChanged(this, null!);
    }

    private TaskRecord? Selected => (Grid.SelectedItem as Row)?.Record;

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var r = Selected;
        var worktreeExists = r?.Worktree is { } wt && Directory.Exists(wt);
        OpenButton.IsEnabled = r != null && !_taskRunning;
        WorktreeButton.IsEnabled = worktreeExists;
        TimelineButton.IsEnabled = r != null;
        RemoveButton.IsEnabled = r != null && !_taskRunning;
        StatusText.Text = _taskRunning
            ? T("Há uma tarefa em andamento: termine ou interrompa para abrir outra.", "A task is running: finish or stop it to open another one.", "Hay una tarea en ejecución: termínela o interrúmpala para abrir otra.")
            : r == null ? "" : worktreeExists ? $"{r.Branch} · {r.Worktree}" : T("O worktree desta tarefa não existe mais.", "This task's worktree no longer exists.", "El worktree de esta tarea ya no existe.");
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r || _taskRunning) return;
        ToOpen = r;
        Close();
    }

    /// <summary>Cada pedido, decisão, fase e commit da tarefa, numa tela (vale para tarefa local ou git).</summary>
    private void OnTimelineClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) new TimelineWindow(r) { Owner = this }.ShowDialog();
    }

    private void OnWorktreeClick(object sender, RoutedEventArgs e)
    {
        if (Selected?.Worktree is { } wt && Directory.Exists(wt))
            Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        var ask = T($"Remover a tarefa \"{r.Info.Title}\"?\n\nIsso apaga o worktree, o branch {r.Branch} e o registro do Histórico. O código que não foi juntado ao seu repositório se perde. Não dá para desfazer.",
                    $"Remove the task \"{r.Info.Title}\"?\n\nThis deletes the worktree, the branch {r.Branch} and the History record. Code not merged into your repository is lost. This cannot be undone.",
                    $"¿Eliminar la tarea \"{r.Info.Title}\"?\n\nEsto borra el worktree, la rama {r.Branch} y el registro del Historial. El código no integrado en su repositorio se pierde. No se puede deshacer.");
        if (MessageBox.Show(this, ask, "NXPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (r.Worktree is { } wt && Directory.Exists(wt))
                await GitWorktree.RemoveAsync(r.Info.RepoPath, wt, r.Branch); // modo local: RepoPath é o repositório-sombra do assunto
            TaskStore.Delete(r.Id);
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = T("Não foi possível remover: ", "Could not remove: ", "No se pudo eliminar: ") + ex.Message;
        }
    }
}
