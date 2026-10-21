// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using NXPilot.Desktop.ViewModels;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Quanto a execução deve levar, para a pessoa saber antes de aprovar o plano: pelas tarefas já
/// concluídas com o mesmo agente — tempo por fase (do "Plano aprovado" à última fase, incluindo as
/// suas aprovações no caminho) e o da verificação. Mediana, para uma tarefa fora da curva não distorcer.
/// </summary>
public static class TaskEstimate
{
    private const int MinSamples = 2;

    public sealed record Estimate(TimeSpan Total, int Samples);

    public static Estimate? For(int phases, ExecutorKind executor, bool withReview)
    {
        if (phases <= 0) return null;
        var perPhase = new List<double>();
        var review = new List<double>();
        foreach (var r in TaskStore.LoadAll().Where(r => r.State == TaskState.Done && r.Info.Executor == executor && r.Phases.Count > 0))
        {
            var approved = r.Log.FirstOrDefault(l => l.Kind is "aprovado" or "approved" or "aprobado"
                && (l.Message.StartsWith("Plano aprovado") || l.Message.StartsWith("Plan approved") || l.Message.StartsWith("Plan aprobado")));
            var lastPhase = r.Log.LastOrDefault(l => l.Kind is "fase" or "phase");
            if (approved == null || lastPhase == null || lastPhase.Time <= approved.Time) continue;
            perPhase.Add((lastPhase.Time - approved.Time).TotalSeconds / r.Phases.Count);
            var verified = r.Log.FirstOrDefault(l => l.Kind is "verificação" or "verification" or "verificación" && l.Time >= lastPhase.Time);
            if (verified != null) review.Add((verified.Time - lastPhase.Time).TotalSeconds);
        }
        if (perPhase.Count < MinSamples) return null;
        var seconds = phases * Median(perPhase) + (withReview && review.Count > 0 ? Median(review) : 0);
        return new Estimate(TimeSpan.FromSeconds(seconds), perPhase.Count);
    }

    /// <summary>"~4 min", "~1 h 10 min", "menos de 1 min".</summary>
    public static string Format(TimeSpan t) => t.TotalMinutes < 1 ? NxText.T("menos de 1 min", "under 1 min", "menos de 1 min")
        : t.TotalHours < 1 ? $"~{Math.Round(t.TotalMinutes):0} min"
        : $"~{(int)t.TotalHours} h {t.Minutes:00} min";

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
