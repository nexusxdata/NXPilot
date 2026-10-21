// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXPilot.Desktop.ViewModels;
using NXPilot.Risk;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Riscos das tarefas (decisões do gate): de uma tarefa ou de todas as que estão na lista do
/// Histórico, filtrando por nível (ALTO, MÉDIO, BAIXO) e pelo que aconteceu com cada ação.
/// </summary>
public partial class RisksWindow : Window
{
    private sealed record Row(RiskEntry Entry, string Task)
    {
        public string When => Entry.Time.ToString("dd/MM HH:mm:ss");
        public string LevelText => LevelName(Entry.Level);
        public Brush LevelBrush => LevelColor(Entry.Level);
        public int LevelOrder => (int)Entry.Level;
        public string Outcome => OutcomeName(Entry.Outcome);
        public string Action => Entry.Action.Replace("\r", " ").Replace("\n", " ");
        public string Agent => Entry.Agent;
        public string Detail => Entry.Reason is { Length: > 0 } r ? $"{Entry.Action}\n\n{r}" : Entry.Action;
    }

    private readonly List<Row> _all;

    /// <param name="tasks">Tarefas (título e riscos); com uma só, o título da janela é o dela.</param>
    /// <param name="level">Filtro inicial de nível (o mesmo do Histórico), nulo = todos.</param>
    public RisksWindow(IReadOnlyList<(string Title, IReadOnlyList<RiskEntry> Risks)> tasks, RiskLevel? level = null)
    {
        InitializeComponent();
        _all = tasks.SelectMany(t => t.Risks.Select(r => new Row(r, t.Title))).OrderByDescending(r => r.Entry.Time).ToList();
        Title = T("Riscos das tarefas", "Task risks", "Riesgos de las tareas");
        TitleText.Text = tasks.Count == 1 ? T($"Riscos · {tasks[0].Title}", $"Risks · {tasks[0].Title}", $"Riesgos · {tasks[0].Title}")
                                          : T($"Riscos de {tasks.Count} tarefa(s)", $"Risks of {tasks.Count} task(s)", $"Riesgos de {tasks.Count} tarea(s)");
        SummaryText.Text = Summary(_all.Select(r => r.Entry).ToList()) + "  ·  " +
            T("BAIXO passa sozinho; MÉDIO e ALTO esperam você (salvo regra da tarefa, que nunca vale para ALTO).",
              "LOW passes on its own; MEDIUM and HIGH wait for you (unless a task rule, which never applies to HIGH).",
              "BAJO pasa solo; MEDIO y ALTO esperan por usted (salvo regla de la tarea, que nunca vale para ALTO).");
        WhenColumn.Header = T("Quando", "When", "Cuándo");
        LevelColumn.Header = T("Risco", "Risk", "Riesgo");
        OutcomeColumn.Header = T("Resultado", "Outcome", "Resultado");
        ActionColumn.Header = T("Ação", "Action", "Acción");
        AgentColumn.Header = T("Agente", "Agent", "Agente");
        TaskColumn.Header = T("Tarefa", "Task", "Tarea");
        TaskColumn.Visibility = tasks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = T("Fechar", "Close", "Cerrar");

        LevelBox.Items.Add(new ComboBoxItem { Content = T("Todos os níveis", "All levels", "Todos los niveles"), Tag = null });
        foreach (var lv in new[] { RiskLevel.Alto, RiskLevel.Medio, RiskLevel.Baixo })
            LevelBox.Items.Add(new ComboBoxItem { Content = $"{LevelName(lv)} ({_all.Count(r => r.Entry.Level == lv)})", Tag = lv, Foreground = LevelColor(lv) });
        LevelBox.SelectedIndex = level is { } l ? l switch { RiskLevel.Alto => 1, RiskLevel.Medio => 2, _ => 3 } : 0;

        OutcomeBox.Items.Add(new ComboBoxItem { Content = T("Qualquer resultado", "Any outcome", "Cualquier resultado"), Tag = null });
        foreach (var o in new[] { "approved", "denied", "rule", "auto" })
            OutcomeBox.Items.Add(new ComboBoxItem { Content = $"{OutcomeName(o)} ({_all.Count(r => r.Entry.Outcome == o)})", Tag = o });
        OutcomeBox.SelectedIndex = 0;
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && OutcomeBox != null && OutcomeBox.Items.Count > 0) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var level = (LevelBox.SelectedItem as ComboBoxItem)?.Tag as RiskLevel?;
        var outcome = (OutcomeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var rows = _all.Where(r => (level == null || r.Entry.Level == level) && (outcome == null || r.Entry.Outcome == outcome)).ToList();
        Grid.ItemsSource = rows;
        EmptyText.Text = _all.Count == 0
            ? T("Nenhuma decisão do gate registrada nesta(s) tarefa(s).", "No gate decision recorded in this task (or tasks).", "Ninguna decisión del gate registrada en esta(s) tarea(s).")
            : T("Nenhum risco com esse filtro.", "No risk matches this filter.", "Ningún riesgo coincide con este filtro.");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- textos e cores (também usados pelo Histórico)

    public static string LevelName(RiskLevel level) => level switch
    {
        RiskLevel.Alto => T("▲ Alto", "▲ High", "▲ Alto"),
        RiskLevel.Medio => T("● Médio", "● Medium", "● Medio"),
        _ => T("▼ Baixo", "▼ Low", "▼ Bajo"),
    };

    public static Brush LevelColor(RiskLevel level) => level switch
    {
        RiskLevel.Alto => new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)),
        RiskLevel.Medio => new SolidColorBrush(Color.FromRgb(0xC2, 0x5E, 0x00)),
        _ => new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34)),
    };

    public static string OutcomeName(string outcome) => outcome switch
    {
        "auto" => T("passou sozinho", "passed on its own", "pasó solo"),
        "rule" => T("regra da tarefa", "task rule", "regla de la tarea"),
        "approved" => T("✓ aprovado por você", "✓ approved by you", "✓ aprobado por usted"),
        "denied" => T("✗ negado por você", "✗ denied by you", "✗ denegado por usted"),
        _ => outcome,
    };

    /// <summary>"Alto 1 · Médio 4 · Baixo 37" (só os que existem); vazio sem decisões.</summary>
    public static string Summary(IReadOnlyCollection<RiskEntry> risks) =>
        risks.Count == 0 ? T("sem decisões do gate", "no gate decisions", "sin decisiones del gate")
            : string.Join(" · ", new[] { RiskLevel.Alto, RiskLevel.Medio, RiskLevel.Baixo }
                .Select(l => (l, n: risks.Count(r => r.Level == l))).Where(x => x.n > 0).Select(x => $"{LevelName(x.l)} {x.n}"));
}
