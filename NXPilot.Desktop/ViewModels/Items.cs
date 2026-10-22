// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using CommunityToolkit.Mvvm.ComponentModel;
using NXPilot.Benchmark;
using NXPilot.Risk;

namespace NXPilot.Desktop.ViewModels;

/// <summary>
/// Linha da barra lateral por agente: login (✓ verde, triângulo vermelho até a pessoa entrar — o clique abre o login,
/// 🚧 em construção) e, abaixo, o que ele está fazendo agora. State: "in", "out", "build" ou "none" (login não conferido).
/// </summary>
public sealed record AgentLoginItem(NXPilot.Agents.AgentKind? Kind, string State, string Title, string Hint, AgentItem? Activity = null,
    string? UpdateTip = null, string? UpdateProgress = null)
{
    /// <summary>Versão nova do agente publicada: triângulo amarelo ao lado do nome (clique pergunta se quer atualizar).</summary>
    public bool HasUpdate => UpdateTip != null && UpdateProgress == null;

    /// <summary>Atualizando agora: barra na linha do agente com a etapa (baixando, conferindo, instalando).</summary>
    public bool IsUpdating => UpdateProgress != null;
    public bool SignedIn => State == "in";
    public bool SignedOut => State == "out";
    public bool Building => State == "build";
    public bool HasActivity => Activity != null;
}

public sealed record AgentItem(string Name, string Status, string Task, string Elapsed, IReadOnlyList<AgentItem>? Children = null);

public sealed record ArtifactItem(string Glyph, string Title, string Subtitle);

/// <summary>
/// Arquivo que a tarefa criou ou alterou. "Abrir" só para documentos (docx, pdf, planilha,
/// imagem, texto…): abrir um .py/.bat/.ps1 pelo Windows executaria o script.
/// </summary>
public sealed record ResultFile(string RelativePath, string FullPath)
{
    private static readonly string[] Documents = [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".pdf", ".csv", ".txt", ".md", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".json", ".xml", ".log", ".pbip"];
    private static readonly string[] Reports = [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".pdf", ".csv", ".png", ".jpg", ".jpeg"];

    public string Extension => System.IO.Path.GetExtension(FullPath).ToLowerInvariant();
    public bool CanOpen => Documents.Contains(Extension);

    /// <summary>Entregáveis (relatório, planilha, PDF, imagem) aparecem primeiro e em destaque.</summary>
    public bool IsDeliverable => Reports.Contains(Extension);

    public string Glyph => Extension switch
    {
        ".docx" or ".doc" or ".pdf" or ".md" or ".txt" => "📄",
        ".xlsx" or ".xls" or ".csv" => "📊",
        ".pptx" => "📽",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" => "🖼",
        _ => "⚙",
    };
}

public sealed record FileChangeItem(string Path, string Kind, string Description);

/// <param name="LastEvent">Último evento do log desta fase (resultado ou erro), mostrado abaixo dela.</param>
public sealed record PhaseItem(int Number, string Title, string Status, string Agent, double Progress, IReadOnlyList<FileChangeItem> Files,
    string LastEvent = "", bool LastIsError = false)
{
    public bool HasLastEvent => LastEvent.Length > 0;

    /// <summary>Status é chave interna (cor do selo); este é o texto exibido.</summary>
    public string StatusLabel => Status switch
    {
        "Concluido" => NxText.T("Concluído", "Done", "Concluido"),
        "Rodando" => NxText.T("Rodando", "Running", "En ejecución"),
        "Pendente" => NxText.T("Pendente", "Pending", "Pendiente"),
        _ => Status
    };
}

/// <summary>Gravidade no estilo da verificação por fase: Critico, Maior, Menor.</summary>
public sealed record VerificationItem(string Severity, string File, string Message);

public sealed record DiffLine(string Text, string Kind);

/// <param name="Phase">Fase em execução quando o evento aconteceu (nula = fora das fases: plano, aprovação…).</param>
public sealed record LogEntry(DateTime Time, string Source, string Kind, string Message, int? Phase = null)
{
    public bool IsError => Kind is "erro" or "error" or "negado" or "denied" or "denegado";

    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>
/// Uma decisão do gate na tarefa (vai para o Histórico): nível do risco e o que aconteceu —
/// "auto" (BAIXO passou sozinho), "rule" (regra da tarefa), "approved"/"denied" (você), "log" (tarefa antiga, lida do Log).
/// </summary>
public sealed record RiskEntry(DateTime Time, string Agent, string Action, NXPilot.Risk.RiskLevel Level, string Outcome, string? Reason = null);

/// <summary>Ação pedida por um agente, aguardando o DecisionGate (classificação + usuário).</summary>
public sealed partial class DecisionItem(string agent, string action, string context) : ObservableObject
{
    public string Agent { get; } = agent;
    public string Action { get; } = action;
    public string Context { get; } = context;
    public DateTime RequestedAt { get; } = DateTime.Now;
    public string TimeText => RequestedAt.ToString("HH:mm:ss");

    /// <summary>
    /// Pedido vindo de um agente REAL (hook PreToolUse): o agente fica parado até Aprovar/Negar
    /// completar esta resposta. Nulo nas ações simuladas.
    /// </summary>
    public TaskCompletionSource<bool>? Response { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseTaskRules))] private RiskLevel? _level;

    /// <summary>Scripts que o comando vai rodar (.py, .ps1…) — botão "Ver script" para ler antes de aprovar.</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];
    public bool HasScripts => Scripts.Count > 0;

    /// <summary>"Aprovar semelhantes" e "aprovar médio e baixo" só aparecem fora do risco ALTO.</summary>
    public bool CanUseTaskRules => Level is RiskLevel.Baixo or RiskLevel.Medio;
    [ObservableProperty] private string _riskDetail = NxText.T("aguardando classificador…", "waiting for the classifier…", "esperando al clasificador…");

    /// <summary>O pedido numa frase para quem não é técnico (o que quer fazer e por que o risco é esse).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPlain))] private string _plain = "";
    public bool HasPlain => Plain.Length > 0;
}

public sealed record BenchmarkRow(string Engine, string Load, double AverageMs, string Average, string P95, string Accuracy,
    int MissedHigh, double BarWidth, string? Error, IReadOnlyList<BenchmarkCase> Cases)
{
    public static BenchmarkRow From(BenchmarkReport r, double maxBar = 220)
    {
        // Barra em escala log: 0,1 ms → quase nada; 10 s → cheia. Linear esconderia o ONNX.
        var w = r.Error != null ? 0 : Math.Clamp((Math.Log10(Math.Max(r.AverageMs, 0.1)) + 1) / 5 * maxBar, 4, maxBar);
        return new BenchmarkRow(AiLabViewModel.ShortEngine(r.Engine), $"{r.Load.TotalMilliseconds:N0} ms", r.AverageMs, FormatMs(r.AverageMs),
            FormatMs(r.P95Ms), r.Error == null ? $"{r.CorrectCount}/{r.Cases.Count}" : "—", r.MissedHigh, w, r.Error, r.Cases);
    }

    public static string FormatMs(double ms) => ms >= 1000 ? $"{ms / 1000:0.00} s" : $"{ms:0.0} ms";
}

/// <summary>Resultado do "testar ação" — um por motor carregado.</summary>
public sealed record TryRow(string Engine, RiskLevel Level, string Confidence, string Time, string? Reason);
