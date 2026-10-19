// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NXPilot.Agents;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Linha do tempo de uma tarefa, em árvore: etapa (Planejamento, Fase N, Verificação, Correção)
/// recolhida com os totais → cada evento numa linha → o texto completo quando você expande. É a
/// trilha de auditoria do Histórico, mais os commits de cada fase com o diff. Vale igual para
/// versionamento local e repositório git.
/// </summary>
public partial class TimelineWindow : Window
{
    private enum Category { Request, YourDecision, Automatic, Phase, Problem, Other }

    private sealed partial class EventRow(string time, string glyph, Brush brush, string source, string kind, string message, Category category) : ObservableObject
    {
        public string Time { get; } = time;
        public string Glyph { get; } = glyph;
        public Brush Brush { get; } = brush;
        public string Source { get; } = source;
        public string Kind { get; } = kind;
        public string Message { get; } = message;
        public Category Category { get; } = category;
        public string OneLine { get; } = Regex.Replace(message, @"\s+", " ");
        [ObservableProperty] private bool _isExpanded;
    }

    private sealed partial class EventGroup(string title) : ObservableObject
    {
        public string Title { get; } = title;
        public List<EventRow> All { get; } = new();
        [ObservableProperty] private List<EventRow> _events = new();
        [ObservableProperty] private string _detail = "";
        [ObservableProperty] private bool _isExpanded;
    }

    private sealed record DiffRow(string Text, Brush Brush);

    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x4A));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0x9A, 0x5B, 0x00));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));

    private readonly TaskRecord _task;
    private readonly List<EventRow> _events;
    private readonly List<EventGroup> _groups;

    public TimelineWindow(TaskRecord task)
    {
        Resources["EventHeaderWidth"] = 820d;
        InitializeComponent();
        _task = task;
        _events = task.Log.OrderBy(l => l.Time).Select(ToRow).ToList();
        _groups = GroupByStage(task, _events);

        TitleText.Text = task.Info.Title;
        var requests = _events.Count(e => e.Category == Category.Request);
        var yours = _events.Count(e => e.Category == Category.YourDecision);
        var auto = _events.Count(e => e.Category == Category.Automatic);
        SummaryText.Text = T($"{task.CreatedAt:dd/MM/yyyy HH:mm} · {requests} pedido(s) que foram para você · {yours} decisão(ões) suas · {auto} aprovação(ões) automática(s) · {task.Phases.Count} fase(s)",
                             $"{task.CreatedAt:dd/MM/yyyy HH:mm} · {requests} request(s) sent to you · {yours} decision(s) of yours · {auto} automatic approval(s) · {task.Phases.Count} phase(s)",
                             $"{task.CreatedAt:dd/MM/yyyy HH:mm} · {requests} solicitud(es) enviadas a usted · {yours} decisión(es) suyas · {auto} aprobación(es) automática(s) · {task.Phases.Count} fase(s)");
        WorktreeButton.IsEnabled = task.Worktree is { } wt && Directory.Exists(wt);
        ApplyFilter();
        Loaded += async (_, _) => await LoadCommitsAsync();
    }

    /// <summary>O tipo do evento vem do texto gravado no log (no idioma em que foi registrado).</summary>
    private static EventRow ToRow(LogEntry l)
    {
        var kind = l.Kind.ToLowerInvariant();
        bool Is(params string[] words) => words.Any(w => kind.Contains(w));
        var you = l.Source is "Você" or "You" or "Usted";

        var (glyph, brush, category) =
            Is("negado", "denied", "denegado") ? ("✖", Red, Category.YourDecision)
            : you && Is("aprovado", "approved", "aprobado", "regra", "rule", "regla", "aplicado", "applied") ? ("✔", Green, Category.YourDecision)
            : Is("auto-", "por regra", "by rule", "por regla") ? ("✓", Gray, Category.Automatic)
            : Is("pedido", "request", "solicitud") ? ("?", Amber, Category.Request)
            : Is("erro", "error", "aviso", "warning", "falh", "fail") ? ("!", Red, Category.Problem)
            : Is("fase", "phase", "verifica", "correção", "fix", "corrección", "snapshot", "worktree") ? ("◆", Blue, Category.Phase)
            : you ? ("•", Blue, Category.YourDecision)
            : ("·", Gray, Category.Other);
        return new EventRow(l.Time.ToString("HH:mm:ss"), glyph, brush, l.Source, l.Kind, l.Message, category);
    }

    /// <summary>
    /// Etapas a partir das marcas do log: "Plano aprovado" fecha o Planejamento; "Fase N
    /// concluída" fecha a Fase N; depois da última fase vem a Verificação; "corrigir achados"
    /// abre uma Correção. Tudo que acontece entre duas marcas pertence à etapa em curso.
    /// </summary>
    private static List<EventGroup> GroupByStage(TaskRecord task, List<EventRow> events)
    {
        var groups = new List<EventGroup>();
        EventGroup Open(string title)
        {
            var g = new EventGroup(title);
            groups.Add(g);
            return g;
        }
        string PhaseTitle(int n) => task.Phases.FirstOrDefault(p => p.Number == n) is { } p
            ? T($"Fase {n}: {p.Title}", $"Phase {n}: {p.Title}", $"Fase {n}: {p.Title}")
            : T($"Fase {n}", $"Phase {n}", $"Fase {n}");

        var current = Open(T("Planejamento", "Planning", "Planificación"));
        var nextPhase = 1;
        var fixes = 0;
        foreach (var e in events)
        {
            var you = e.Source is "Você" or "You" or "Usted";
            var kind = e.Kind.ToLowerInvariant();
            if (you && Regex.IsMatch(kind, "corrigir|fix findings|corregir"))
            {
                fixes++;
                current = Open(T($"Correção dos achados{(fixes > 1 ? $" ({fixes})" : "")}", $"Fixing findings{(fixes > 1 ? $" ({fixes})" : "")}", $"Corrección de hallazgos{(fixes > 1 ? $" ({fixes})" : "")}"));
            }
            if (Regex.IsMatch(kind, "verifica") && !current.Title.StartsWith(T("Verificação", "Verification", "Verificación")))
                current = Open(T("Verificação cruzada", "Cross-verification", "Verificación cruzada"));

            current.All.Add(e);

            if (you && Regex.IsMatch(e.Message, "^(Plano aprovado|Plan approved|Plan aprobado)") && task.Phases.Count > 0)
                current = Open(PhaseTitle(nextPhase));
            else if (Regex.IsMatch(kind, "^(fase|phase)$") && Regex.Match(e.Message, @"^(Fase|Phase) (\d+)") is { Success: true } m)
            {
                nextPhase = int.Parse(m.Groups[2].Value) + 1;
                if (nextPhase <= task.Phases.Count) current = Open(PhaseTitle(nextPhase));
            }
        }
        groups.RemoveAll(g => g.All.Count == 0);
        foreach (var g in groups)
        {
            var asked = g.All.Count(e => e.Category == Category.Request);
            var yours = g.All.Count(e => e.Category == Category.YourDecision);
            var auto = g.All.Count(e => e.Category == Category.Automatic);
            var problems = g.All.Count(e => e.Category == Category.Problem);
            g.Detail = $"{g.All[0].Time}–{g.All[^1].Time} · " + T(
                $"{g.All.Count} evento(s) · {asked} pedido(s) · {yours} sua(s) · {auto} automática(s){(problems > 0 ? $" · {problems} erro(s)/aviso(s)" : "")}",
                $"{g.All.Count} event(s) · {asked} request(s) · {yours} yours · {auto} automatic{(problems > 0 ? $" · {problems} error(s)/warning(s)" : "")}",
                $"{g.All.Count} evento(s) · {asked} solicitud(es) · {yours} suya(s) · {auto} automática(s){(problems > 0 ? $" · {problems} error(es)/aviso(s)" : "")}");
        }
        return groups;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (GroupList != null) ApplyFilter();
    }

    /// <summary>Filtra os eventos dentro de cada etapa; etapa sem nada no filtro some.</summary>
    private void ApplyFilter()
    {
        Func<EventRow, bool> keep =
            FilterDecisions.IsChecked == true ? r => r.Category is Category.Request or Category.YourDecision or Category.Automatic
            : FilterPhases.IsChecked == true ? r => r.Category == Category.Phase
            : FilterProblems.IsChecked == true ? r => r.Category == Category.Problem
            : _ => true;
        foreach (var g in _groups) g.Events = g.All.Where(keep).ToList();
        var visible = _groups.Where(g => g.Events.Count > 0).ToList();
        GroupList.ItemsSource = visible;
        var shown = visible.Sum(g => g.Events.Count);
        StatusText.Text = T($"{shown} de {_events.Count} evento(s) em {visible.Count} etapa(s)", $"{shown} of {_events.Count} event(s) in {visible.Count} stage(s)", $"{shown} de {_events.Count} evento(s) en {visible.Count} etapa(s)");
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups) g.IsExpanded = true;
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var g in _groups)
        {
            g.IsExpanded = false;
            foreach (var ev in g.All) ev.IsExpanded = false;
        }
    }

    /// <summary>A linha do evento acompanha a largura da janela (o cabeçalho do Expander não estica sozinho).</summary>
    private void OnEventsSizeChanged(object sender, SizeChangedEventArgs e) =>
        Resources["EventHeaderWidth"] = Math.Max(400, e.NewSize.Width - 110);

    private async Task LoadCommitsAsync()
    {
        IReadOnlyList<TaskCommit> commits = [];
        if (_task.Worktree is { } wt && Directory.Exists(wt))
        {
            try { commits = await GitWorktree.CommitsAsync(wt, _task.BaseCommit); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }
        CommitList.ItemsSource = commits;
        NoCommitsText.Visibility = commits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommitList.Visibility = commits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (commits.Count > 0) CommitList.SelectedIndex = 0;
    }

    private async void OnCommitChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CommitList.SelectedItem is not TaskCommit c || _task.Worktree is not { } wt) return;
        try
        {
            var diff = await GitWorktree.CommitDiffAsync(wt, c.Sha);
            DiffList.ItemsSource = diff.Split('\n').Select(line => new DiffRow(line.TrimEnd('\r'),
                line.StartsWith("+++") || line.StartsWith("---") || line.StartsWith("diff ") ? Blue
                : line.StartsWith('+') ? Green
                : line.StartsWith('-') ? Red
                : line.StartsWith("@@") ? Amber
                : Brushes.Black)).ToList();
        }
        catch (Exception ex) { DiffList.ItemsSource = new[] { new DiffRow(ex.Message, Red) }; }
    }

    private void OnWorktreeClick(object sender, RoutedEventArgs e)
    {
        if (_task.Worktree is { } wt && Directory.Exists(wt)) Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
