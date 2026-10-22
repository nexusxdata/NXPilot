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
/// Histórico de tarefas. Abrir mostra a tarefa na tela (somente leitura); Lixo só tira da lista
/// (exclusão lógica, volta com Restaurar); remover (em "⋯ Mais") apaga o worktree, o branch
/// nxpilot/&lt;id&gt; e o registro — com confirmação, porque não volta.
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
        ("trash", "🗑", "#888888", () => T("No lixo", "In the trash", "En la papelera")),
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
        var trash = TaskTrash.Load();
        var rows = TaskStore.LoadAll().Select(r =>
        {
            var session = TaskSession.FromRecord(r);
            var label = session.StateLabel;
            var findings = r.Verification is { } v
                ? T($"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crit. · {v.Findings.Count} total", $"{v.Findings.Count(f => f.Severity == "Critico")} crít. · {v.Findings.Count} total")
                : "—";
            var st = Statuses.First(x => x.Key == (trash.ContainsKey(r.Id) ? "trash" : StatusKeyOf(r)));
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
            .Where(r => status == "all" ? r.StatusKey != "trash" : r.StatusKey == status) // o lixo só aparece no filtro dele
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
        FindingsButton.IsEnabled = r is { Verification: not null } || r?.FixRounds is { Count: > 0 };
        CloneButton.IsEnabled = r != null && !_taskRunning;
        TimelineButton.IsEnabled = r != null;
        MoreButton.Content = T("⋯ Mais", "⋯ More", "⋯ Más");
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

    /// <summary>Lixo / Restaurar: exclusão lógica — só tira da lista (ou devolve); nada é apagado.</summary>
    private void OnTrashClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Row row || _taskRunning) return;
        try
        {
            if (row.StatusKey == "trash") TaskTrash.Restore(row.Record.Id);
            else TaskTrash.Move(row.Record.Id);
            Reload();
            StatusText.Text = row.StatusKey == "trash"
                ? T($"\"{row.Record.Info.Title}\" voltou para a lista.", $"\"{row.Record.Info.Title}\" is back in the list.", $"\"{row.Record.Info.Title}\" volvió a la lista.")
                : T($"\"{row.Record.Info.Title}\" foi para o lixo (nada foi apagado). Para ver: filtro de estado \"🗑 No lixo\".",
                    $"\"{row.Record.Info.Title}\" went to the trash (nothing was deleted). To see it: state filter \"🗑 In the trash\".",
                    $"\"{row.Record.Info.Title}\" fue a la papelera (no se borró nada). Para verla: filtro de estado \"🗑 En la papelera\".");
        }
        catch (Exception ex)
        {
            StatusText.Text = T("Não foi possível mover para o lixo: ", "Could not move to the trash: ", "No se pudo mover a la papelera: ") + ex.Message;
        }
    }

    /// <summary>"⋯ Mais": o que se usa menos (riscos, worktree, Lixo/Restaurar e a remoção definitiva).</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var r = Selected;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        System.Windows.Controls.MenuItem Item(string key, RoutedEventHandler click, bool enabled, string? tipKey = null)
        {
            var item = new System.Windows.Controls.MenuItem { Header = FindResource(key), IsEnabled = enabled };
            if (tipKey != null) item.ToolTip = FindResource(tipKey);
            item.Click += click;
            return item;
        }
        menu.Items.Add(Item("Hist_VerRiscos", OnRisksClick, r != null, "Hist_VerRiscosDica"));
        menu.Items.Add(Item("Hist_RiscosDaLista", OnAllRisksClick, Grid.Items.Count > 0, "Hist_RiscosDaListaDica"));
        menu.Items.Add(Item("Hist_AbrirWorktree", OnWorktreeClick, r?.Worktree is { } wt && Directory.Exists(wt)));
        menu.Items.Add(new System.Windows.Controls.Separator());
        // Lixo = exclusão lógica (volta pelo filtro de estado "🗑 No lixo" → Restaurar).
        var inTrash = (Grid.SelectedItem as Row)?.StatusKey == "trash";
        var trash = new System.Windows.Controls.MenuItem
        {
            Header = inTrash ? T("♻ Restaurar", "♻ Restore", "♻ Restaurar") : T("🗑 Mover para o lixo", "🗑 Move to trash", "🗑 Mover a la papelera"),
            ToolTip = inTrash
                ? T("Volta a tarefa para a lista.", "Puts the task back in the list.", "Devuelve la tarea a la lista.")
                : T("Tira a tarefa da lista sem apagar nada (exclusão lógica). Veja no filtro de estado \"🗑 No lixo\".",
                    "Takes the task off the list without deleting anything (soft delete). See it in the state filter \"🗑 In the trash\".",
                    "Quita la tarea de la lista sin borrar nada (eliminación lógica). Véala en el filtro de estado \"🗑 En la papelera\"."),
            IsEnabled = r != null && !_taskRunning,
        };
        trash.Click += OnTrashClick;
        menu.Items.Add(trash);
        // Remover de vez só o que está no lixo há pelo menos 48 h: primeiro a exclusão lógica, depois a física.
        var readyAt = DateTime.MaxValue;
        var canRemove = r != null && CanRemove(r.Id, out readyAt);
        var remove = Item("Hist_Remover", OnRemoveClick, canRemove && !_taskRunning);
        remove.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        // Dica sempre visível (mesmo desabilitado): a regra e onde a tarefa está nela.
        var rule = T("Remoção definitiva: apaga o worktree, o branch e o registro — não dá para desfazer.\n" +
                     "Regra: 1) mova a tarefa para o lixo (⋯ Mais → 🗑 Mover para o lixo); 2) ela fica 48 h no lixo, podendo ser restaurada " +
                     "(filtro de estado \"🗑 No lixo\" → ♻ Restaurar); 3) depois de 48 h a remoção é liberada.",
                     "Permanent removal: deletes the worktree, the branch and the record — it cannot be undone.\n" +
                     "Rule: 1) move the task to the trash (⋯ More → 🗑 Move to trash); 2) it stays 48 h in the trash and can be restored " +
                     "(state filter \"🗑 In the trash\" → ♻ Restore); 3) after 48 h removal is allowed.",
                     "Eliminación definitiva: borra el worktree, la rama y el registro — no se puede deshacer.\n" +
                     "Regla: 1) mueva la tarea a la papelera (⋯ Más → 🗑 Mover a la papelera); 2) queda 48 h en la papelera y se puede restaurar " +
                     "(filtro de estado \"🗑 En la papelera\" → ♻ Restaurar); 3) después de 48 h se permite eliminar.");
        var now = r == null ? "" : !inTrash
            ? T("Esta tarefa: ainda não está no lixo.", "This task: not in the trash yet.", "Esta tarea: aún no está en la papelera.")
            : !canRemove
                ? T($"Esta tarefa: no lixo; remoção liberada a partir de {readyAt:dd/MM HH:mm}.", $"This task: in the trash; removal allowed from {readyAt:MM/dd HH:mm}.", $"Esta tarea: en la papelera; eliminación permitida desde {readyAt:dd/MM HH:mm}.")
                : T("Esta tarefa: no lixo há mais de 48 h — pode remover.", "This task: in the trash for over 48 h — can be removed.", "Esta tarea: en la papelera hace más de 48 h — se puede eliminar.");
        remove.ToolTip = new System.Windows.Controls.TextBlock { Text = now.Length > 0 ? rule + "\n\n" + now : rule, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(remove, true);
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    /// <summary>Tempo mínimo no lixo antes da remoção definitiva (dá para se arrepender).</summary>
    private static readonly TimeSpan TrashGrace = TimeSpan.FromHours(48);

    /// <summary>Só remove de vez o que está no lixo há pelo menos <see cref="TrashGrace"/>; devolve quando fica liberado.</summary>
    private static bool CanRemove(string id, out DateTime readyAt)
    {
        readyAt = DateTime.MaxValue;
        if (!TaskTrash.Load().TryGetValue(id, out var trashedAt)) return false;
        readyAt = trashedAt + TrashGrace;
        return DateTime.Now >= readyAt;
    }

    /// <summary>
    /// Imagens da pessoa que a tarefa copiou para a pasta de imagens (log "… → caminho"): apaga as que nenhuma
    /// outra tarefa usa. Só a cópia — o arquivo original da pessoa não é tocado. Devolve quantas apagou.
    /// </summary>
    private static int DeleteOwnImages(TaskRecord r)
    {
        var prefixes = new[] { "Imagem própria guardada na pasta de imagens → ", "Own image kept in the images folder → ", "Imagen propia guardada en la carpeta de imágenes → " };
        var files = r.Log.Select(l => prefixes.FirstOrDefault(l.Message.StartsWith) is { } p ? l.Message[p.Length..].Trim() : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        if (files.Count == 0) return 0;
        var others = TaskStore.LoadAll().Where(o => o.Id != r.Id).SelectMany(o => o.Log).Select(l => l.Message).ToList();
        var deleted = 0;
        foreach (var f in files.Where(f => !others.Any(m => m.Contains(f, StringComparison.OrdinalIgnoreCase))))
        {
            try { File.Delete(f); deleted++; }
            catch { /* em uso ou sem permissão: fica na pasta */ }
        }
        return deleted;
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r || !CanRemove(r.Id, out _)) return; // só do lixo, depois de 48 h
        var ask = T($"Remover a tarefa \"{r.Info.Title}\"?\n\nIsso apaga o worktree, o branch {r.Branch} e o registro do Histórico. O código que não foi juntado ao seu repositório se perde. Não dá para desfazer.",
                    $"Remove the task \"{r.Info.Title}\"?\n\nThis deletes the worktree, the branch {r.Branch} and the History record. Code not merged into your repository is lost. This cannot be undone.",
                    $"¿Eliminar la tarea \"{r.Info.Title}\"?\n\nEsto borra el worktree, la rama {r.Branch} y el registro del Historial. El código no integrado en su repositorio se pierde. No se puede deshacer.");
        if (MessageBox.Show(this, ask, "NXPilot", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (r.Worktree is { } wt && Directory.Exists(wt))
                await GitWorktree.RemoveAsync(r.Info.RepoPath, wt, r.Branch); // modo local: RepoPath é o repositório-sombra do assunto
            var images = DeleteOwnImages(r);
            TaskStore.Delete(r.Id);
            TaskTrash.Restore(r.Id); // removida de vez: sai do lixo também
            Reload();
            if (images > 0)
                StatusText.Text = T($"Removida. {images} imagem(ns) sua(s) que esta tarefa copiou para a pasta de imagens também foram apagadas (o original continua onde estava).",
                    $"Removed. {images} image(s) of yours that this task copied to the images folder were deleted too (the original stays where it was).",
                    $"Eliminada. {images} imagen(es) suya(s) que esta tarea copió a la carpeta de imágenes también se borraron (el original sigue donde estaba).");
        }
        catch (Exception ex)
        {
            StatusText.Text = T("Não foi possível remover: ", "Could not remove: ", "No se pudo eliminar: ") + ex.Message;
        }
    }
}
