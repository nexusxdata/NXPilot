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
    private sealed record Row(TaskRecord Record, string When, string Title, string State, string Findings, string Cost, string Repo,
        string StatusKey, string Status, System.Windows.Media.Brush StatusBrush, IReadOnlyList<RiskEntry> Risks)
    {
        private string Count(NXPilot.Risk.RiskLevel level, string glyph) => Risks.Count(r => r.Level == level) is > 0 and var n ? $"{glyph}{n}" : "";
        public string HighText => Count(NXPilot.Risk.RiskLevel.Alto, "▲");
        public string MediumText => Count(NXPilot.Risk.RiskLevel.Medio, "●");
        public string LowText => Count(NXPilot.Risk.RiskLevel.Baixo, "▼");
        public string NoRiskText => Risks.Count == 0 ? "—" : "";
        public string RiskTip => RisksWindow.Summary(Risks.ToList());
        /// <summary>Ordenar a coluna: mais ALTO primeiro, depois MÉDIO.</summary>
        public int RiskOrder => Risks.Count(r => r.Level == NXPilot.Risk.RiskLevel.Alto) * 1_000_000 + Risks.Count(r => r.Level == NXPilot.Risk.RiskLevel.Medio) * 1000 + Risks.Count;
    }

    /// <summary>
    /// Estado curto da tarefa (coluna e filtro): em andamento, aguardando, concluída, aviso
    /// (concluída com achados da verificação), erro, interrompida, descartada, em escuta.
    /// </summary>
    private static readonly (string Key, string Icon, string Color, Func<string> Label)[] Statuses =
    [
        ("running", "⟳", "#1F6FB2", () => T("Em andamento", "Running", "En curso")),
        ("waiting", "⏸", "#8A6D00", () => T("Aguardando você", "Waiting for you", "Esperando por usted")),
        ("done", "✓", "#1E7B34", () => T("Concluída", "Done", "Concluida")),
        ("warning", "⚠", "#C25E00", () => T("Aviso", "Warning", "Aviso")),
        ("error", "✗", "#B3261E", () => T("Erro", "Error", "Error")),
        ("stopped", "■", "#666666", () => T("Interrompida", "Stopped", "Interrumpida")),
        ("discarded", "–", "#888888", () => T("Descartada", "Discarded", "Descartada")),
        ("listening", "⚡", "#6A3FB5", () => T("Em escuta", "Listening", "En escucha")),
    ];

    private static string StatusKeyOf(TaskRecord r) => r.State switch
    {
        TaskState.Preparing or TaskState.Planning or TaskState.Executing or TaskState.Verifying => "running",
        TaskState.AwaitingApproval => "waiting",
        TaskState.Done when r.Verification is { Approved: false } => "warning",
        TaskState.Done => "done",
        TaskState.Failed => "error",
        TaskState.Discarded => "discarded",
        TaskState.Armed => "listening",
        _ => "stopped",
    };

    private readonly bool _taskRunning;

    /// <summary>Tarefa escolhida para abrir na tela principal; nula = só fechou.</summary>
    public TaskRecord? ToOpen { get; private set; }

    /// <summary>Tarefa escolhida para clonar: a Nova tarefa abre preenchida com os dados dela.</summary>
    public TaskRecord? ToClone { get; private set; }

    public HistoryWindow(bool taskRunning)
    {
        InitializeComponent();
        _taskRunning = taskRunning;
        StatusBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Todos os estados", "All states", "Todos los estados"), Tag = "all" });
        foreach (var st in Statuses)
            StatusBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{st.Icon}  {st.Label()}", Tag = st.Key });
        StatusBox.SelectedIndex = 0;
        RiskBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Qualquer risco", "Any risk", "Cualquier riesgo"), Tag = "all" });
        foreach (var lv in new[] { NXPilot.Risk.RiskLevel.Alto, NXPilot.Risk.RiskLevel.Medio, NXPilot.Risk.RiskLevel.Baixo })
            RiskBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Com ", "With ", "Con ") + RisksWindow.LevelName(lv), Tag = lv.ToString(), Foreground = RisksWindow.LevelColor(lv) });
        RiskBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Sem decisões do gate", "No gate decisions", "Sin decisiones del gate"), Tag = "none" });
        RiskBox.SelectedIndex = 0;
        Reload();
    }

    private void Reload()
    {
        var rows = TaskStore.LoadAll().Select(r =>
        {
            var session = TaskSession.FromRecord(r);
            var label = session.StateLabel;
            var findings = r.Verification is { } v
                ? T($"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crit. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total")
                : "—";
            var st = Statuses.First(x => x.Key == StatusKeyOf(r));
            var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(st.Color));
            return new Row(r, r.CreatedAt.ToString("dd/MM HH:mm"), (r.Info.AsTemplate ? "⚡ " : "") + r.Info.Title, label, findings,
                r.Tokens is { Total: > 0 } t ? NXPilot.Agents.TokenUsage.Format(t.Total) : "—", r.Info.Subject is { } subject ? "◆ " + subject : r.Info.RepoPath,
                st.Key, $"{st.Icon}  {st.Label()}", brush, session.Risks.ToList());
        }).ToList();
        _allRows = rows;
        FillSubjects();
        ApplyFilter();
    }

    /// <summary>Chave do assunto da tarefa (a pasta; tarefa em repositório git não tem assunto).</summary>
    private const string NoSubject = "(sem assunto)";
    private static string SubjectKey(TaskRecord r) => r.Info.SubjectFolder ?? r.Info.Subject ?? NoSubject;

    /// <summary>Assuntos que aparecem no histórico (mantém a escolha ao recarregar).</summary>
    private void FillSubjects()
    {
        var keep = (SubjectBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        SubjectBox.Items.Clear();
        SubjectBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Todos os assuntos", "All subjects", "Todos los asuntos"), Tag = "all" });
        foreach (var g in _allRows.Where(r => SubjectKey(r.Record) != NoSubject).GroupBy(r => SubjectKey(r.Record))
                     .OrderBy(g => g.First().Record.Info.Subject ?? g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = g.First().Record.Info.Subject ?? Path.GetFileName(g.Key.TrimEnd('\\', '/'));
            SubjectBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"◆ {name} ({g.Count()})", Tag = g.Key, ToolTip = g.Key });
        }
        if (_allRows.Any(r => SubjectKey(r.Record) == NoSubject))
            SubjectBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = T("Sem assunto (repositório git)", "No subject (git repository)", "Sin asunto (repositorio git)"), Tag = NoSubject });
        SubjectBox.SelectedItem = SubjectBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep) ?? SubjectBox.Items[0];
    }

    private List<Row> _allRows = new();

    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || SearchBox == null || PeriodBox == null || ToDate == null || TypeBox == null || StatusBox == null || RiskBox == null || SubjectBox == null) return; // SelectionChanged dispara durante o InitializeComponent
        CustomPeriodPanel.Visibility = PeriodTag == "custom" ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    /// <summary>Busca dinâmica a partir desta quantidade de letras; com menos, a lista mostra tudo.</summary>
    private const int MinSearchLength = 3;

    /// <summary>Filtra enquanto você digita: em tudo (título, intenção, critérios, estado, pasta) ou num campo só.</summary>
    private void ApplyFilter()
    {
        var term = SearchBox.Text.Trim();
        if (term.Length < MinSearchLength) term = "";
        var field = (SearchFieldBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        var (from, to) = PeriodRange();
        var type = (TypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        var status = (StatusBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        var risk = (RiskBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        var subject = (SubjectBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "all";
        var inPeriod = _allRows.Where(r => (from == null || r.Record.CreatedAt >= from) && (to == null || r.Record.CreatedAt < to))
            .Where(r => type switch { "hooks" => r.Record.Info.AsTemplate, "tasks" => !r.Record.Info.AsTemplate, _ => true })
            .Where(r => status == "all" || r.StatusKey == status)
            .Where(r => subject == "all" || SubjectKey(r.Record) == subject)
            .Where(r => risk switch
            {
                "all" => true,
                "none" => r.Risks.Count == 0,
                _ => r.Risks.Any(x => x.Level.ToString() == risk),
            })
            .ToList();
        var rows = term.Length == 0 ? inPeriod : inPeriod.Where(r => field switch
        {
            "title" => Matches(r.Record.Info.Title, term),
            "intent" => Matches(r.Record.Info.Intent, term),
            "criteria" => Matches(r.Record.Info.Criteria, term),
            _ => Matches(r.Record.Info.Title, term) || Matches(r.Record.Info.Intent, term) || Matches(r.Record.Info.Criteria, term)
                 || Matches(r.State, term) || Matches(r.Status, term) || Matches(r.Repo, term),
        }).ToList();
        var selected = Selected;
        Grid.ItemsSource = rows;
        EmptyText.Text = _allRows.Count == 0
            ? T("Nenhuma tarefa ainda. Use + Nova tarefa.", "No tasks yet. Use + New task.", "Aún no hay tareas. Use + Nueva tarea.")
            : T("Nenhuma tarefa encontrada com essa busca, período, assunto, estado ou risco.", "No task matches this search, period, subject, state or risk.", "Ninguna tarea coincide con esta búsqueda, período, asunto, estado o riesgo.");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) Grid.SelectedItem = rows.FirstOrDefault(r => r.Record == selected) ?? rows[0];
        OnSelectionChanged(this, null!);
    }

    private string PeriodTag => (PeriodBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "any";

    /// <summary>Início (inclusive) e fim (exclusivo) do período escolhido; nulo = sem limite daquele lado.</summary>
    private (DateTime? From, DateTime? To) PeriodRange()
    {
        var today = DateTime.Today;
        return PeriodTag switch
        {
            "today" => (today, null),
            "7" => (today.AddDays(-6), null),
            "30" => (today.AddDays(-29), null),
            "month" => (new DateTime(today.Year, today.Month, 1), null),
            "custom" => (FromDate.SelectedDate?.Date, ToDate.SelectedDate?.Date.AddDays(1)), // "até" inclui o dia inteiro
            _ => (null, null),
        };
    }

    /// <summary>Contém o termo, sem diferenciar maiúsculas nem acentos ("intencao" acha "intenção").</summary>
    private static bool Matches(string? text, string term) =>
        text != null && System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, term,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    private TaskRecord? Selected => (Grid.SelectedItem as Row)?.Record;

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var r = Selected;
        var worktreeExists = r?.Worktree is { } wt && Directory.Exists(wt);
        OpenButton.IsEnabled = r != null && !_taskRunning;
        WorktreeButton.IsEnabled = worktreeExists;
        TimelineButton.IsEnabled = r != null;
        RisksButton.IsEnabled = r != null;
        FindingsButton.IsEnabled = r is { Verification: not null } || r?.FixRounds is { Count: > 0 };
        AllRisksButton.IsEnabled = Grid.Items.Count > 0;
        CloneButton.IsEnabled = r != null && !_taskRunning;
        RemoveButton.IsEnabled = r != null && !_taskRunning;
        StatusText.Text = _taskRunning
            ? T("Há uma tarefa em andamento: termine ou interrompa para abrir outra.", "A task is running: finish or stop it to open another one.", "Hay una tarea en ejecución: termínela o interrúmpala para abrir otra.")
            : r == null ? "" : worktreeExists ? $"{r.Branch} · {r.Worktree}" : T("O worktree desta tarefa não existe mais.", "This task's worktree no longer exists.", "El worktree de esta tarea ya no existe.");
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridClicks.OnRow(e)) OnOpenClick(sender, e);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r || _taskRunning) return;
        ToOpen = r;
        Close();
    }

    private void OnCloneClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r || _taskRunning) return;
        ToClone = r;
        Close();
    }

    /// <summary>Cada pedido, decisão, fase e commit da tarefa, numa tela (vale para tarefa local ou git).</summary>
    /// <summary>Riscos da tarefa selecionada (já no nível escolhido no filtro, se houver).</summary>
    private void OnRisksClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Row row) new RisksWindow([(row.Record.Info.Title, row.Risks)], FilterLevel) { Owner = this }.ShowDialog();
    }

    /// <summary>Achados da verificação e as rodadas de correção (o que foi achado e o que foi corrigido).</summary>
    private void OnFindingsClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) new FindingsWindow(r) { Owner = this }.ShowDialog();
    }

    /// <summary>Riscos de todas as tarefas que estão na lista (com os filtros aplicados).</summary>
    private void OnAllRisksClick(object sender, RoutedEventArgs e)
    {
        var rows = (Grid.ItemsSource as IEnumerable<Row>)?.ToList() ?? new();
        if (rows.Count > 0) new RisksWindow(rows.Select(r => (r.Record.Info.Title, r.Risks)).ToList(), FilterLevel) { Owner = this }.ShowDialog();
    }

    private NXPilot.Risk.RiskLevel? FilterLevel =>
        Enum.TryParse<NXPilot.Risk.RiskLevel>((RiskBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string, out var lv) ? lv : null;

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
