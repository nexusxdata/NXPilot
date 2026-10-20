// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXPilot.WindowsUi;

/// <summary>
/// Quais aplicativos o NXPilot pode ver. Lista por NOME DO PROCESSO (ex.: PBIDesktop, EXCEL),
/// sem ".exe" e sem diferenciar maiúsculas. Fora da lista, nada é lido nem capturado — nem por
/// engano (a janela do e-mail ou do banco nunca vai parar no prompt de um agente).
/// Sem dependências (o teste crítico compila este arquivo direto).
/// </summary>
public sealed class WindowsUiPolicy
{
    private readonly HashSet<string> _allowed;

    public WindowsUiPolicy(IEnumerable<string> allowedProcesses)
    {
        _allowed = new HashSet<string>(allowedProcesses.Select(Normalize).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Só o Power BI Desktop (o que a extensão do Power BI usa).</summary>
    public static WindowsUiPolicy PowerBiOnly => new(["PBIDesktop"]);

    public IReadOnlyCollection<string> Allowed => _allowed;

    public bool IsAllowed(string? processName) => processName is { Length: > 0 } && _allowed.Contains(Normalize(processName));

    /// <summary>"C:\…\EXCEL.EXE" / "excel.exe " → "excel".</summary>
    public static string Normalize(string processName)
    {
        var p = Path.GetFileName(processName.Trim());
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }

    /// <summary>Uma por linha, vírgula ou ponto e vírgula (como digitado na tela).</summary>
    public static WindowsUiPolicy Parse(string text) =>
        new(text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed class WindowsUiDeniedException(string processName)
    : Exception($"O aplicativo '{processName}' não está liberado na Extensão MCP → Janelas do Windows. Nada foi lido nem capturado.")
{
    public string ProcessName { get; } = processName;
}
