// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using CommunityToolkit.Mvvm.ComponentModel;
using NXPilot.Agents;
using static NXPilot.NxText;

namespace NXPilot.Desktop.ViewModels;

/// <summary>Armed: tarefa modelo aprovada e em modo escuta (não executa; cada evento dispara uma execução).</summary>
public enum TaskState { Preparing, Planning, AwaitingApproval, Executing, Verifying, Done, Failed, Discarded, Stopped, Armed }

/// <summary>Quem revisa o resultado: Codex (somente leitura), o próprio Claude, ou ninguém.</summary>
public enum ReviewerKind { Codex, Claude, None }

/// <summary>Quem executa a tarefa (planeja e escreve) — sempre sob o DecisionGate.</summary>
public enum ExecutorKind { Claude, Codex }

/// <summary>O que a pessoa preencheu no assistente de Nova tarefa.</summary>
/// <param name="RepoPath">Raiz do repositório git, ou o repositório-sombra do assunto (modo local).</param>
/// <param name="Versioning">Tarefas antigas (sem o campo) eram sempre git.</param>
/// <param name="Subject">Modo local: nome do assunto.</param>
/// <param name="SubjectFolder">Modo local: pasta do assunto (para onde o resultado volta).</param>
/// <param name="AsTemplate">Tarefa HOOK: depois do plano roda UMA execução de teste (você aprova os riscos) e então é armada (modo escuta).</param>
/// <param name="TemplateId">Tarefa comum disparada por um hook (nulo quando não veio de hook).</param>
public sealed record NewTaskInfo(string RepoPath, string Title, string Intent, string Criteria, string? Model,
    ReviewerKind Reviewer = ReviewerKind.Codex, string? CodexModel = null, ExecutorKind Executor = ExecutorKind.Claude,
    VersioningMode Versioning = VersioningMode.Git, string? Subject = null, string? SubjectFolder = null,
    bool AsTemplate = false, string? TemplateId = null);

/// <summary>Registro salvo no Histórico (%LOCALAPPDATA%\NXPilot\tasks\&lt;id&gt;.json).</summary>
public sealed record TaskRecord(
    string Id, NewTaskInfo Info, TaskState State, DateTime CreatedAt, DateTime UpdatedAt,
    string? Worktree, string? Branch, string? BaseCommit, string? SessionId,
    string PlanText, List<PlannedPhase> Phases, List<string> PhaseStatus,
    VerificationResult? Verification, decimal CostUsd, List<LogEntry> Log, TokenUsage? Tokens = null,
    List<string>? PreApprovals = null);

/// <summary>
/// Uma tarefa real: intenção → plano em fases (agente em modo plan) → sua aprovação → fases
/// executadas no worktree (um commit por fase) → verificação cruzada → diff. Cada estado
/// libera só os botões que fazem sentido nele.
/// </summary>
public sealed partial class TaskSession(NewTaskInfo info, string id) : ObservableObject
{
    public string Id { get; } = id;
    public NewTaskInfo Info { get; private set; } = info;
    public string Title => Info.Title;

    /// <summary>Editar antes de tentar de novo (título, intenção, critérios, agente, modelos, revisor).</summary>
    public void UpdateInfo(NewTaskInfo info)
    {
        Info = info;
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ApproveLabel));
    }
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateLabel), nameof(CanApprove), nameof(IsRunning), nameof(CanDiscard), nameof(IsFinished), nameof(CanApplyToFolder), nameof(CanResume), nameof(CanRetry), nameof(HasError), nameof(CanFixFindings), nameof(CanArm))]
    private TaskState _state = TaskState.Preparing;

    [ObservableProperty] private string? _worktree;
    [ObservableProperty] private string? _branch;
    [ObservableProperty] private string? _baseCommit;
    [ObservableProperty] private string? _sessionId;
    [ObservableProperty] private string _planText = "";

    /// <summary>Motivo da última falha, mostrado no card junto do Tentar novamente; nulo sem erro.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _lastError;
    public bool HasError => State == TaskState.Failed && !string.IsNullOrWhiteSpace(LastError);

    /// <summary>Projeto Power BI (.pbip) encontrado no worktree; nulo se não for um projeto PBIP.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPowerBi))] private NXPilot.Integrations.PowerBiProject? _powerBi;
    public bool HasPowerBi => PowerBi != null;
    /// <summary>Estimativa em dólar que o Claude informa (preço de API). Guardada, mas não exibida: em plano de assinatura não é o que você paga.</summary>
    [ObservableProperty] private decimal _costUsd;

    /// <summary>Tokens somados de todas as execuções da tarefa (planejar, fases, correção, verificação).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CostLabel))] private TokenUsage? _tokens;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateLabel), nameof(CanFixFindings))] private VerificationResult? _verification;

    /// <summary>Quando a etapa que está rodando começou (planejar, executar, corrigir, verificar); nulo parada.</summary>
    public DateTime? RunningSince { get; private set; }

    /// <summary>Cronômetro da etapa em andamento ("03:12"), atualizado a cada segundo pelo ViewModel.</summary>
    [ObservableProperty] private string _elapsedText = "";

    partial void OnStateChanged(TaskState value)
    {
        if (IsRunning) RunningSince ??= DateTime.Now;
        else { RunningSince = null; ElapsedText = ""; }
    }

    public void Tick()
    {
        if (RunningSince is not { } since) return;
        var e = DateTime.Now - since;
        ElapsedText = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
    }

    /// <summary>Executando a correção dos achados (não uma fase do plano).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StateLabel))] private bool _fixingFindings;

    /// <summary>Fases do plano e o status de cada uma ("Pendente", "Rodando", "Concluido").</summary>
    public List<PlannedPhase> Phases { get; set; } = new();
    public List<string> PhaseStatus { get; set; } = new();

    /// <summary>Log desta tarefa — vai para o Histórico (trilha de auditoria por tarefa).</summary>
    public List<LogEntry> Log { get; } = new();

    public bool CanApprove => State == TaskState.AwaitingApproval;
    public bool CanDiscard => State == TaskState.AwaitingApproval;
    public bool IsRunning => State is TaskState.Preparing or TaskState.Planning or TaskState.Executing or TaskState.Verifying;
    /// <summary>Interrompida ou falhou já com plano e o worktree ainda lá: dá para continuar de onde parou.</summary>
    public bool CanResume => State is TaskState.Stopped or TaskState.Failed && Phases.Count > 0 && Worktree is { } wt && System.IO.Directory.Exists(wt);

    /// <summary>Falhou ou foi interrompida antes de ter plano (worktree, agente, leitura do plano): refaz e pede o plano de novo.</summary>
    public bool CanRetry => State is TaskState.Stopped or TaskState.Failed && Phases.Count == 0;

    /// <summary>Concluída, mas a verificação não aprovou: dá para devolver os achados ao executor.</summary>
    public bool CanFixFindings => State == TaskState.Done && Verification is { Approved: false, Findings.Count: > 0 } && Worktree is { } wt && System.IO.Directory.Exists(wt);

    /// <summary>Modo local: o resultado só chega à pasta original quando você manda aplicar.</summary>
    public bool CanApplyToFolder => Info.Versioning == VersioningMode.Local && State == TaskState.Done;
    public bool IsFinished => State is TaskState.Done or TaskState.Failed or TaskState.Discarded or TaskState.Stopped or TaskState.Armed;

    /// <summary>Hook: assinaturas das ações que você aprovou na execução de teste (viram pré-aprovação nos disparos).</summary>
    public HashSet<string> PreApprovals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hook com a execução de teste concluída: dá para armar (modo escuta).</summary>
    public bool CanArm => Info.AsTemplate && State == TaskState.Done;

    /// <summary>Texto do botão de aprovar: num modelo, a aprovação arma (modo escuta) em vez de executar.</summary>
    public string ApproveLabel => Info.AsTemplate
        ? T("✓  Aprovar e fazer a execução de teste", "✓  Approve and do the test run", "✓  Aprobar y hacer la ejecución de prueba")
        : T("✓  Aprovar plano e executar", "✓  Approve plan and run", "✓  Aprobar plan y ejecutar");

    public string CostLabel => Tokens is { Total: > 0 } t
        ? T($"tokens: {TokenUsage.Format(t.Output)} gerados · {TokenUsage.Format(t.Input + t.CacheWrite)} lidos · {TokenUsage.Format(t.CacheRead)} do cache",
            $"tokens: {TokenUsage.Format(t.Output)} generated · {TokenUsage.Format(t.Input + t.CacheWrite)} read · {TokenUsage.Format(t.CacheRead)} from cache",
            $"tokens: {TokenUsage.Format(t.Output)} generados · {TokenUsage.Format(t.Input + t.CacheWrite)} leídos · {TokenUsage.Format(t.CacheRead)} de caché")
        : "";

    public string StateLabel => State switch
    {
        TaskState.Preparing => T("Preparando o worktree…", "Preparing the worktree…", "Preparando el worktree…"),
        TaskState.Planning => T("Claude Code está planejando (somente leitura)…", "Claude Code is planning (read-only)…", "Claude Code está planificando (solo lectura)…"),
        TaskState.AwaitingApproval when Info.AsTemplate => T($"Plano do hook pronto ({Phases.Count} fase(s)) — aprove para a execução de teste (os riscos que você aprovar nela ficam pré-aprovados)", $"Hook plan ready ({Phases.Count} phase(s)) — approve for the test run (the risks you approve in it become pre-approved)", $"Plan del hook listo ({Phases.Count} fase(s)) — apruebe para la ejecución de prueba (los riesgos que apruebe quedan preaprobados)"),
        TaskState.Done when Info.AsTemplate => T("Execução de teste concluída — confira e clique em ⚡ Armar hook (modo escuta)", "Test run done — check it and click ⚡ Arm hook (listening mode)", "Ejecución de prueba concluida — revísela y haga clic en ⚡ Armar hook (modo escucha)"),
        TaskState.AwaitingApproval => T($"Plano pronto ({Phases.Count} fase(s)) — aguardando a sua aprovação", $"Plan ready ({Phases.Count} phase(s)) — waiting for your approval", $"Plan listo ({Phases.Count} fase(s)) — esperando su aprobación"),
        TaskState.Executing when FixingFindings => T("Analisando e corrigindo os achados da verificação — ações passam pelo gate", "Analyzing and fixing the verification findings — actions go through the gate", "Analizando y corrigiendo los hallazgos de la verificación — las acciones pasan por el gate"),
        TaskState.Executing => T($"Executando a fase {PhaseStatus.IndexOf("Rodando") + 1} de {Phases.Count} — ações passam pelo gate", $"Running phase {PhaseStatus.IndexOf("Rodando") + 1} of {Phases.Count} — actions go through the gate", $"Ejecutando la fase {PhaseStatus.IndexOf("Rodando") + 1} de {Phases.Count} — las acciones pasan por el gate"),
        TaskState.Verifying => T("Verificação cruzada: outro agente revisa o diff (somente leitura)…", "Cross-verification: another agent reviews the diff (read-only)…", "Verificación cruzada: otro agente revisa el diff (solo lectura)…"),
        TaskState.Done when Verification is { Approved: false } v => T($"Concluída, mas a verificação apontou {v.Findings.Count(f => f.Severity == "Critico")} crítico(s) — revise antes de usar", $"Done, but verification found {v.Findings.Count(f => f.Severity == "Critico")} critical issue(s) — review before using", $"Concluida, pero la verificación encontró {v.Findings.Count(f => f.Severity == "Critico")} problema(s) crítico(s) — revise antes de usar"),
        TaskState.Done => T("Concluída e verificada — veja o resultado na aba Diff", "Done and verified — see the result in the Diff tab", "Concluida y verificada — vea el resultado en la pestaña Diff"),
        TaskState.Failed => T("Falhou — veja o Terminal e o Log", "Failed — see the Terminal and the Log", "Falló — vea el Terminal y el Log"),
        TaskState.Discarded => T("Plano descartado", "Plan discarded", "Plan descartado"),
        TaskState.Armed => T("⚡ Hook armado — em modo escuta: cada evento dispara uma tarefa nova (veja ⚡ Hooks)", "⚡ Hook armed — listening: each event triggers a new task (see ⚡ Hooks)", "⚡ Hook armado — en modo escucha: cada evento dispara una tarea nueva (vea ⚡ Hooks)"),
        TaskState.Stopped => T("Interrompida por você", "Stopped by you", "Interrumpida por usted"),
        _ => State.ToString()
    };

    /// <summary>Atualiza os textos calculados (troca de idioma, mudança de fase).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(CostLabel));
    }

    public TaskRecord ToRecord() => new(Id, Info, State, CreatedAt, DateTime.Now, Worktree, Branch, BaseCommit, SessionId,
        PlanText, Phases, PhaseStatus, Verification, CostUsd, Log.ToList(), Tokens, PreApprovals.Count > 0 ? PreApprovals.ToList() : null);

    public static TaskSession FromRecord(TaskRecord r)
    {
        var s = new TaskSession(r.Info, r.Id)
        {
            CreatedAt = r.CreatedAt,
            Worktree = r.Worktree, Branch = r.Branch, BaseCommit = r.BaseCommit, SessionId = r.SessionId,
            PlanText = r.PlanText, CostUsd = r.CostUsd, Tokens = r.Tokens, Verification = r.Verification,
            Phases = r.Phases, PhaseStatus = r.PhaseStatus,
            // Tarefa que estava rodando quando o app fechou não continua sozinha.
            State = r.State is TaskState.Preparing or TaskState.Planning or TaskState.Executing or TaskState.Verifying ? TaskState.Stopped : r.State,
        };
        s.Log.AddRange(r.Log);
        foreach (var a in r.PreApprovals ?? new()) s.PreApprovals.Add(a);
        if (s.State == TaskState.Failed) s.LastError = r.Log.LastOrDefault(l => l.Kind is "erro" or "error")?.Message;
        return s;
    }
}
