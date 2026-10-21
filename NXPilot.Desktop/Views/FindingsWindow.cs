// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXPilot.Agents;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Histórico → Achados e correções: o que a verificação cruzada achou em cada rodada, o que o
/// executor fez (resumo e commit) e o resultado da verificação atual — para conferir sem abrir a tarefa.
/// </summary>
public sealed class FindingsWindow : Window
{
    public FindingsWindow(TaskRecord record)
    {
        Title = T("Achados e correções — ", "Findings and fixes — ", "Hallazgos y correcciones — ") + record.Info.Title;
        Width = 900; Height = 640; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 8) };
        body.Children.Add(new TextBlock
        {
            Text = T("Cada rodada mostra os achados que a verificação devolveu e o que o agente fez com eles. O commit de cada correção fica no worktree da tarefa.",
                     "Each round shows the findings the verification returned and what the agent did with them. Each fix commit is in the task worktree.",
                     "Cada ronda muestra los hallazgos que devolvió la verificación y lo que hizo el agente con ellos. El commit de cada corrección queda en el worktree de la tarea."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12),
        });

        var rounds = record.FixRounds ?? new();
        if (rounds.Count == 0)
            body.Children.Add(Muted(record.Verification == null
                ? T("Esta tarefa não tem verificação cruzada.", "This task has no cross-verification.", "Esta tarea no tiene verificación cruzada.")
                : T("Nenhuma correção feita ainda (use Analisar e corrigir achados na tarefa, ou ligue a opção automática na Nova tarefa).",
                    "No fix yet (use Analyze and fix findings in the task, or turn on the automatic option in New task).",
                    "Aún no hay correcciones (use Analizar y corregir hallazgos en la tarea, o active la opción automática en Nueva tarea).")));

        foreach (var r in rounds)
        {
            var card = Card();
            card.Children.Add(Header(T($"Rodada {r.Number}", $"Round {r.Number}", $"Ronda {r.Number}")
                + $" · {r.Started:dd/MM HH:mm} · " + (r.Automatic ? T("automática", "automatic", "automática") : T("pedida por você", "requested by you", "pedida por usted"))));
            card.Children.Add(Label(T($"Achados enviados ao agente ({r.Before.Findings.Count}) — revisor {r.Before.Reviewer}:",
                $"Findings sent to the agent ({r.Before.Findings.Count}) — reviewer {r.Before.Reviewer}:", $"Hallazgos enviados al agente ({r.Before.Findings.Count}) — revisor {r.Before.Reviewer}:")));
            foreach (var f in r.Before.Findings) card.Children.Add(FindingLine(f));
            card.Children.Add(Label(T("O que o agente fez:", "What the agent did:", "Lo que hizo el agente:")));
            card.Children.Add(r.Finished == null
                ? Muted(T("(rodada não terminou — interrompida ou falhou)", "(round did not finish — stopped or failed)", "(la ronda no terminó — interrumpida o falló)"))
                : Text((r.Committed ? T("✓ Commit de correção no worktree. ", "✓ Fix committed in the worktree. ", "✓ Commit de corrección en el worktree. ")
                                    : T("Sem alterações nos arquivos. ", "No file changes. ", "Sin cambios en los archivos. ")) + r.Summary));
            body.Children.Add(card);
        }

        if (record.Verification is { } v)
        {
            var final = Card();
            final.Children.Add(Header(T("Verificação atual", "Current verification", "Verificación actual") + $" · {v.Reviewer}"));
            final.Children.Add(new TextBlock
            {
                Text = (v.Approved ? T("✓ Aprovada", "✓ Approved", "✓ Aprobada") : T("✗ Não aprovada", "✗ Not approved", "✗ No aprobada")) + " — " + v.Summary,
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
                Foreground = v.Approved ? new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34)) : new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)),
            });
            if (v.Findings.Count > 0) final.Children.Add(Label(T($"Achados que continuam ({v.Findings.Count}):", $"Remaining findings ({v.Findings.Count}):", $"Hallazgos que siguen ({v.Findings.Count}):")));
            foreach (var f in v.Findings) final.Children.Add(FindingLine(f));
            body.Children.Add(final);
        }

        var close = new Button { Content = T("Fechar", "Close", "Cerrar"), Padding = new Thickness(16, 5, 16, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 20, 14), IsCancel = true };
        close.Click += (_, _) => Close();
        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static StackPanel Card() => new() { Margin = new Thickness(0, 0, 0, 12) };

    private static UIElement Header(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFB)), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6),
        Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x24, 0x52, 0x7A)) },
    };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 3) };

    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>"[Crítico] arquivo:linha — mensagem", com a cor da gravidade.</summary>
    private static TextBlock FindingLine(Finding f)
    {
        var color = f.Severity switch
        {
            "Critico" => Color.FromRgb(0xB3, 0x26, 0x1E),
            "Maior" => Color.FromRgb(0xC2, 0x5E, 0x00),
            _ => Color.FromRgb(0x55, 0x55, 0x55),
        };
        var label = f.Severity switch { "Critico" => T("Crítico", "Critical", "Crítico"), "Maior" => T("Maior", "Major", "Mayor"), _ => T("Menor", "Minor", "Menor") };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 3) };
        tb.Inlines.Add(new System.Windows.Documents.Run($"[{label}] ") { Foreground = new SolidColorBrush(color), FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new System.Windows.Documents.Run((f.Line is { } l ? $"{f.File}:{l}" : f.File) + " — ") { FontFamily = new FontFamily("Consolas") });
        tb.Inlines.Add(new System.Windows.Documents.Run(f.Message));
        return tb;
    }
}
