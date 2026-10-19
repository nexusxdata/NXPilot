// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXPilot.Agents;

/// <summary>Planejar (só lê e propõe) ou executar (escreve, sob o DecisionGate).</summary>
public enum AgentMode { Plan, Execute }

public enum AgentEventKind { Started, Text, ToolUse, ToolResult, Result, Error, Stderr }

/// <summary>
/// Evento do agente no formato do NXPilot — a tela, o log e o orquestrador não conhecem o
/// stream-json do Claude nem o JSON-RPC do Codex. Mesmo princípio da fachada de mensagens.
/// </summary>
public sealed record AgentEvent(
    AgentEventKind Kind,
    string Text,
    string? ToolName = null,
    string? SessionId = null,
    bool IsError = false,
    decimal? CostUsd = null,
    string? Raw = null,
    TokenUsage? Tokens = null);

/// <summary>
/// Tokens de UMA execução do agente (o que ela consumiu, não o acumulado da sessão). Lidos do
/// cache são bem mais baratos/rápidos e por isso aparecem separados.
/// </summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
    public static TokenUsage operator -(TokenUsage a, TokenUsage b) =>
        new(Math.Max(0, a.Input - b.Input), Math.Max(0, a.Output - b.Output), Math.Max(0, a.CacheRead - b.CacheRead), Math.Max(0, a.CacheWrite - b.CacheWrite));

    /// <summary>"1,2 mi" / "85 mil" / "930".</summary>
    public static string Format(long n) => n >= 1_000_000 ? $"{n / 1_000_000d:0.#} mi" : n >= 1_000 ? $"{n / 1_000d:0} mil" : n.ToString();
}

/// <summary>Pedido ao agente: o prompt, onde trabalhar e se é planejamento ou execução.</summary>
public sealed record AgentRequest(
    string Prompt,
    string WorkingDirectory,
    AgentMode Mode,
    string? ResumeSessionId = null,
    string? Model = null);

/// <summary>
/// Contrato comum dos agentes (Claude Code agora; Codex pelo mesmo contrato depois). Cada
/// ação de ferramenta passa pelo DecisionGate do NXPilot antes de rodar — o adaptador é
/// responsável por ligar esse portão no agente (no Claude Code: hook PreToolUse).
/// </summary>
public interface IAgentAdapter
{
    string Name { get; }

    /// <summary>Executável encontrado nesta máquina; nulo = agente não instalado.</summary>
    string? ExecutablePath { get; }

    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct = default);
}
