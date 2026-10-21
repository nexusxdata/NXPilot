// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Media;
using NXPilot.Desktop.Services;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// ⚡ Hooks: tarefas em modo escuta. Disparar manualmente (com o {texto}), pausar/armar, limites de
/// frequência (abaixo do intervalo ou acima do limite por hora o disparo pede aprovação) e remover.
/// Cada disparo cria uma tarefa comum, que aparece no Histórico.
/// </summary>
public partial class HooksWindow : Window
{
    private sealed record Row(TaskTemplate Hook, string Title, string Subject, string State, string Runs, string Limits, string PreApprovals);

    private readonly MainViewModel _vm;

    public HooksWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Title = Heading.Text = "⚡ Hooks";
        SubHeading.Text = T("Tarefas em modo escuta: cada evento (monitor de apps, manual…) cria uma tarefa comum com o plano do hook. Pede você só se disparar abaixo do intervalo mínimo, acima do limite por hora, ou com um risco ALTO que não estava no teste.",
                            "Listening tasks: each event (app monitor, manual…) creates a regular task with the hook's plan. It only asks you if it fires below the minimum interval, above the hourly limit, or with a HIGH risk that was not in the test.",
                            "Tareas en modo escucha: cada evento (monitor de apps, manual…) crea una tarea común con el plan del hook. Solo le pregunta si se dispara por debajo del intervalo mínimo, por encima del límite por hora, o con un riesgo ALTO que no estaba en la prueba.");
        ColTitle.Header = "Hook";
        ColSubject.Header = T("Assunto", "Subject", "Asunto");
        ColState.Header = T("Estado", "State", "Estado");
        ColRuns.Header = T("Disparos", "Triggers", "Disparos");
        ColLimits.Header = T("Limites", "Limits", "Límites");
        ColPre.Header = T("Pré-aprovadas", "Pre-approved", "Preaprobadas");
        LimitsLabel.Text = T("Limites do hook escolhido:", "Limits of the selected hook:", "Límites del hook elegido:");
        MinIntervalUnit.Text = T("s entre disparos (mínimo)", "s between triggers (minimum)", "s entre disparos (mínimo)");
        MaxPerHourUnit.Text = T("disparos por hora (máximo)", "triggers per hour (maximum)", "disparos por hora (máximo)");
        SaveLimitsButton.Content = T("Salvar limites", "Save limits", "Guardar límites");
        RemoveButton.Content = T("Remover hook…", "Remove hook…", "Quitar hook…");
        FireButton.Content = T("⚡ Disparar agora…", "⚡ Trigger now…", "⚡ Disparar ahora…");
        CloseButton.Content = T("Fechar", "Close", "Cerrar");
        EmptyText.Text = T("Nenhum hook ainda. Na + Nova tarefa, em Opções, escolha o modo ⚡ Hook: o agente planeja, você faz a execução de teste e arma.",
                           "No hooks yet. In + New task, under Options, pick the ⚡ Hook mode: the agent plans, you do the test run and arm it.",
                           "Aún no hay hooks. En + Nueva tarea, en Opciones, elija el modo ⚡ Hook: el agente planifica, usted hace la ejecución de prueba y lo arma.");
        Reload();
    }

    private void Reload(string? selectId = null)
    {
        var rows = TemplateStore.LoadAll().Select(h => new Row(h, h.Title,
            h.Info.SubjectFolder is { } f ? TeamsNotifier.GroupedName(f) : h.Info.RepoPath,
            h.Armed ? T("⚡ armado", "⚡ armed", "⚡ armado") : T("pausado", "paused", "pausado"),
            h.RunCount == 0 ? "—" : T($"{h.RunCount} · último {h.Fires.DefaultIfEmpty().Max():dd/MM HH:mm}", $"{h.RunCount} · last {h.Fires.DefaultIfEmpty().Max():MM/dd HH:mm}", $"{h.RunCount} · último {h.Fires.DefaultIfEmpty().Max():dd/MM HH:mm}"),
            $"{h.MinIntervalSeconds} s · {h.MaxPerHour}/h",
            h.PreApprovals.Count.ToString())).ToList();
        Grid.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SelectedItem = rows.FirstOrDefault(r => r.Hook.Id == selectId) ?? rows.FirstOrDefault();
        OnSelectionChanged(this, null!);
    }

    private TaskTemplate? Selected => (Grid.SelectedItem as Row)?.Hook;

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var h = Selected;
        RemoveButton.IsEnabled = ArmButton.IsEnabled = SaveLimitsButton.IsEnabled = MinIntervalBox.IsEnabled = MaxPerHourBox.IsEnabled = h != null;
        FireButton.IsEnabled = h is { Armed: true };
        ArmButton.Content = h is { Armed: false } ? T("⚡ Armar", "⚡ Arm", "⚡ Armar") : T("Pausar", "Pause", "Pausar");
        MinIntervalBox.Text = h?.MinIntervalSeconds.ToString() ?? "";
        MaxPerHourBox.Text = h?.MaxPerHour.ToString() ?? "";
        StatusText.Text = h == null ? "" : T($"Plano com {h.Phases.Count} fase(s) · criado em {h.CreatedAt:dd/MM HH:mm} · {(_vm.HookQueueCount > 0 ? $"{_vm.HookQueueCount} disparo(s) na fila" : "fila vazia")}",
                                             $"Plan with {h.Phases.Count} phase(s) · created {h.CreatedAt:MM/dd HH:mm} · {(_vm.HookQueueCount > 0 ? $"{_vm.HookQueueCount} trigger(s) queued" : "queue empty")}",
                                             $"Plan con {h.Phases.Count} fase(s) · creado el {h.CreatedAt:dd/MM HH:mm} · {(_vm.HookQueueCount > 0 ? $"{_vm.HookQueueCount} disparo(s) en cola" : "cola vacía")}");
    }

    /// <summary>Disparo manual: pede o {texto} e cria a tarefa (a janela fecha para você acompanhar).</summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridClicks.OnRow(e)) OnFireClick(sender, e);
    }

    private async void OnFireClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Armed: true } hook) return;
        var text = TextPromptWindow.Ask(this, T($"Disparar \"{hook.Title}\"", $"Trigger \"{hook.Title}\"", $"Disparar \"{hook.Title}\""),
            T("Texto do evento (entra no lugar de {texto}, como dado). Pode ficar vazio.", "Event text (replaces {texto}, as data). It may be empty.", "Texto del evento (reemplaza {texto}, como dato). Puede quedar vacío."),
            T("⚡ Disparar", "⚡ Trigger", "⚡ Disparar"));
        if (text == null) return;
        Close();
        await _vm.FireHookAsync(hook.Id, text, T("manual", "manual", "manual"));
    }

    private void OnArmClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } hook) return;
        hook.Armed = !hook.Armed;
        if (hook.Armed) hook.ArmedAt = DateTime.Now;
        TemplateStore.Save(hook);
        Reload(hook.Id);
    }

    private void OnSaveLimitsClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } hook) return;
        if (!int.TryParse(MinIntervalBox.Text, out var seconds) || seconds < 0 || !int.TryParse(MaxPerHourBox.Text, out var perHour) || perHour < 1)
        {
            StatusText.Text = T("Use números: segundos ≥ 0 e disparos por hora ≥ 1.", "Use numbers: seconds ≥ 0 and triggers per hour ≥ 1.", "Use números: segundos ≥ 0 y disparos por hora ≥ 1.");
            StatusText.Foreground = Brushes.Firebrick;
            return;
        }
        hook.MinIntervalSeconds = seconds;
        hook.MaxPerHour = perHour;
        TemplateStore.Save(hook);
        Reload(hook.Id);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } hook) return;
        if (MessageBox.Show(this, T($"Remover o hook \"{hook.Title}\"? Ele para de escutar; as tarefas já disparadas continuam no Histórico.", $"Remove the hook \"{hook.Title}\"? It stops listening; tasks already triggered stay in History.", $"¿Quitar el hook \"{hook.Title}\"? Deja de escuchar; las tareas ya disparadas siguen en el Historial."),
                "NXPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        TemplateStore.Delete(hook.Id);
        Reload();
    }
}
