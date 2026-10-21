// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NXPilot.Risk;

/// <summary>
/// Regras determinísticas: instantâneas e auditáveis, mas só reconhecem o que foi escrito
/// nelas. <see cref="TryMatch"/> devolve nulo quando nenhuma regra casa — é aí que o
/// classificador híbrido passa a vez ao modelo.
/// </summary>
public sealed class RuleRiskClassifier : IRiskClassifier
{
    // Motivo nos três idiomas: a lista é estática, o idioma é escolhido na hora de exibir.
    private sealed record Rule(RiskLevel Level, Regex Pattern, string ReasonPt, string ReasonEn, string ReasonEs)
    {
        public string Reason => NxText.T(ReasonPt, ReasonEn, ReasonEs);
    }

    private static Rule R(RiskLevel level, string pattern, string reasonPt, string reasonEn, string reasonEs) =>
        new(level, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), reasonPt, reasonEn, reasonEs);

    // Ordem importa: a primeira regra que casa vence, por isso Alto vem antes de Médio/Baixo.
    private static readonly Rule[] Rules =
    {
        R(RiskLevel.Alto, @"\brm\s+-[a-z]*r[a-z]*f|\brm\s+-[a-z]*f[a-z]*r", "remoção recursiva forçada", "forced recursive delete", "eliminación recursiva forzada"),
        R(RiskLevel.Alto, @"Remove-Item\b.*-Recurse|\b(del|rd|rmdir)\s+/s", "remoção recursiva (Windows)", "recursive delete (Windows)", "eliminación recursiva (Windows)"),
        R(RiskLevel.Alto, @"git\s+push\b.*(--force|\s-f\b)", "push forçado reescreve o remoto", "forced push rewrites the remote", "push forzado reescribe el remoto"),
        R(RiskLevel.Alto, @"git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f", "descarta trabalho local", "discards local work", "descarta trabajo local"),
        R(RiskLevel.Alto, @"\b(drop\s+(table|database)|truncate\s+table)\b", "destrói dados no banco", "destroys database data", "destruye datos en la base de datos"),
        R(RiskLevel.Alto, @"(curl|wget|iwr|Invoke-WebRequest)\b.*\|\s*(sh|bash|iex|Invoke-Expression)", "executa script baixado", "runs a downloaded script", "ejecuta un script descargado"),
        R(RiskLevel.Alto, @"\bsudo\b|\bchmod\s+777\b|\bshutdown\b|\breg\s+delete\b", "privilégio/sistema", "privilege/system", "privilegio/sistema"),
        R(RiskLevel.Alto, @"\.env\b|secrets?\.(json|ya?ml)|id_rsa", "toca segredos", "touches secrets", "toca secretos"),
        R(RiskLevel.Alto, @"\bnpm\s+publish\b|\bdotnet\s+nuget\s+push\b", "publica pacote", "publishes a package", "publica un paquete"),
        // Conexões e tokens ficam com o NXPilot/NXProject: o agente usa as extensões (com o gate), nunca os segredos direto.
        // Por NOME de arquivo — os arquivos do worktree (também em AppData\Local\NXPilot) não entram.
        R(RiskLevel.Alto, @"config_nxproject\.json|NXProject[^\\/\s]*[\\/](ai-settings|config)[^\\/\s]*\.json|NXPilot[\\/](ai-provider|databricks|entra-token|git-repos|mail|teams|whatsapp|work-items|windows-ui|windows-processes|powerbi|settings)\.json|NXPilot[\\/]MCP[\\/]Settings\b|NXPilot[\\/](mail|tokens?)[\\/]|\.claude[\\/]\.credentials\.json|\.codex[\\/]auth\.json|\.git-credentials|[\\/]gh[\\/]hosts\.yml",
            "lê/altera conexões e tokens do NXPilot, do NXProject ou dos agentes", "reads/changes NXPilot, NXProject or agent connections and tokens", "lee/modifica conexiones y tokens de NXPilot, NXProject o los agentes"),
        R(RiskLevel.Alto, @"ProtectedData|CryptUnprotectData|DataProtectionScope|ConvertFrom-SecureString|\bcmdkey\b|\bvaultcmd\b|Get-StoredCredential",
            "decifra segredos do Windows (DPAPI/cofre)", "decrypts Windows secrets (DPAPI/vault)", "descifra secretos de Windows (DPAPI/almacén)"),
        R(RiskLevel.Alto, @"NXProject\.(Shared|Community)\.dll|NXPilot\.(Core|Mail|GitRepos|Databricks|WindowsUi)\.dll|\bNxPilot\.exe\b",
            "carrega o NXProject/NXPilot por fora das extensões", "loads NXProject/NXPilot outside the extensions", "carga NXProject/NXPilot por fuera de las extensiones"),
        R(RiskLevel.Alto, @"(curl|wget|\biwr\b|\birm\b|Invoke-RestMethod|Invoke-WebRequest|HttpClient|requests\.|fetch\()[^\r\n]*(dev\.azure\.com|visualstudio\.com|api\.github\.com|graph\.microsoft\.com|googleapis\.com|azuredatabricks\.net|webhook\.office\.com)|\baz\s+(boards|devops|repos)\b|\bgh\s+(api|project|issue)\b",
            "chama a API do DevOps/GitHub/Microsoft/Google direto (fora das extensões)", "calls the DevOps/GitHub/Microsoft/Google API directly (outside the extensions)", "llama a la API de DevOps/GitHub/Microsoft/Google directo (fuera de las extensiones)"),

        R(RiskLevel.Medio, @"\b(npm|pnpm|yarn)\s+(install|add|i)\b|\bpip\s+install\b|\bdotnet\s+add\b", "instala dependência", "installs a dependency", "instala una dependencia"),
        R(RiskLevel.Medio, @"git\s+(commit|push|merge|rebase)\b", "altera histórico/remoto", "changes history/remote", "modifica historial/remoto"),
        R(RiskLevel.Medio, @"^(Edit|Write|MultiEdit)\s*:", "escreve arquivo no worktree", "writes a file in the worktree", "escribe un archivo en el worktree"),
        R(RiskLevel.Medio, @"^Delete\s*:", "remove arquivo do worktree", "deletes a file in the worktree", "elimina un archivo del worktree"),
        R(RiskLevel.Medio, @"\b(mkdir|New-Item|mv|Move-Item|cp|Copy-Item)\b", "altera arquivos", "changes files", "modifica archivos"),
        // Rodar script/interpretador executa código arbitrário: nunca é "só leitura".
        R(RiskLevel.Medio, @"^(Bash|PowerShell):\s*(pwsh|powershell|bash|sh|python3?|node|ruby|perl|\.[\\/])", "executa script/programa", "runs a script/program", "ejecuta un script/programa"),

        R(RiskLevel.Baixo, @"^(Read|Glob|Grep|WebFetch|WebSearch)\s*:", "somente leitura", "read-only", "solo lectura"),
        R(RiskLevel.Baixo, @"git\s+(status|diff|log|show|blame|ls-files|rev-parse)\b", "git somente leitura", "read-only git", "git de solo lectura"),
        R(RiskLevel.Baixo, @"^(Bash|PowerShell):\s*(ls|dir|cat|pwd|echo|type|cd|Set-Location|Get-Content|Get-ChildItem|Get-Location|rg|grep)\b", "comando de leitura", "read command", "comando de lectura"),
        R(RiskLevel.Baixo, @"^(Bash|PowerShell):\s*(head|tail|wc|sort|uniq|od|cut|tr|less|more|findstr|which|where|stat|file|tree|Select-Object|Select-String|Measure-Object|Format-\w+|Out-String)\b", "filtro de leitura", "read filter", "filtro de lectura"),
        R(RiskLevel.Baixo, @"\b(dotnet\s+(build|test)|npm\s+(test|run\s+(lint|test|build)))\b", "build/teste", "build/test", "build/prueba"),
    };

    // Separadores de comando composto (;  &&  ||  |  quebra de linha).
    private static readonly Regex Separators = new(@"\s*(?:;|&&|\|\||\||\r?\n)\s*", RegexOptions.Compiled);
    private static readonly Regex ShellPrefix = new(@"^(Bash|PowerShell):\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => NxText.T("Regras", "Rules", "Reglas");

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Nulo quando nenhuma regra reconhece a ação.
    ///
    /// Comando composto (a; b && c | d): primeiro procura ALTO no texto inteiro (pega
    /// "curl … | bash"); depois cada parte vale por si e o resultado é a PIOR delas. Uma parte
    /// desconhecida torna o todo MÉDIO — um `git status` no fim não pode liberar o resto.
    /// </summary>
    public RiskResult? TryMatch(string action)
    {
        var sw = Stopwatch.StartNew();
        if (MatchFirst(action, onlyHigh: true) is { } high)
            return new RiskResult(RiskLevel.Alto, 1.0, Name, high.Reason, sw.Elapsed);

        var prefix = ShellPrefix.Match(action);
        if (prefix.Success)
        {
            var parts = Separators.Split(action[prefix.Length..]).Where(p => p.Length > 0).ToList();
            if (parts.Count > 1)
            {
                var matched = parts.Select(p => MatchFirst(prefix.Value + p, onlyHigh: false)).ToList();
                if (matched.Any(m => m == null))
                    return new RiskResult(RiskLevel.Medio, 0.5, Name,
                        NxText.T("comando composto com parte não reconhecida", "compound command with an unrecognized part", "comando compuesto con una parte no reconocida"), sw.Elapsed);
                var worst = matched.MaxBy(m => m!.Level)!;
                return new RiskResult(worst.Level, 1.0, Name,
                    NxText.T("comando composto: ", "compound command: ", "comando compuesto: ") + string.Join(" + ", matched.Select(m => m!.Reason).Distinct()), sw.Elapsed);
            }
        }

        return MatchFirst(action, onlyHigh: false) is { } rule
            ? new RiskResult(rule.Level, 1.0, Name, rule.Reason, sw.Elapsed)
            : null;
    }

    private static Rule? MatchFirst(string text, bool onlyHigh) =>
        Rules.FirstOrDefault(r => (!onlyHigh || r.Level == RiskLevel.Alto) && r.Pattern.IsMatch(text));

    /// <summary>Sem regra, chuta Médio com confiança zero (nunca aprova sozinho).</summary>
    public Task<RiskResult> ClassifyAsync(string action, CancellationToken ct = default) =>
        Task.FromResult(TryMatch(action) ?? new RiskResult(RiskLevel.Medio, 0, Name, NxText.T("nenhuma regra casou", "no rule matched", "ninguna regla coincidió"), TimeSpan.Zero));

    public void Dispose() { }
}

/// <summary>Regras primeiro; o que elas não reconhecem vai para o modelo (ONNX ou LLM).</summary>
public sealed class HybridRiskClassifier(RuleRiskClassifier rules, IRiskClassifier model) : IRiskClassifier
{
    public string Name => $"{NxText.T("Regras", "Rules", "Reglas")} + {model.Name}";

    public Task InitializeAsync(CancellationToken ct = default) => model.InitializeAsync(ct);

    public async Task<RiskResult> ClassifyAsync(string action, CancellationToken ct = default)
    {
        var hit = rules.TryMatch(action);
        if (hit != null) return hit with { Engine = Name };
        var r = await model.ClassifyAsync(action, ct);
        return r with { Engine = Name };
    }

    public void Dispose() => model.Dispose();
}

/// <summary>
/// Cascata: regras (µs) → modelo rápido (ms) → modelo lento (s) só quando o rápido tem
/// confiança abaixo de <paramref name="threshold"/>. Paga o LLM apenas nos casos difíceis.
/// </summary>
public sealed class CascadeRiskClassifier(RuleRiskClassifier rules, IRiskClassifier fast, IRiskClassifier slow, double threshold = 0.6)
    : IRiskClassifier
{
    public string Name => $"{NxText.T("Regras", "Rules", "Reglas")} → {fast.Name} → {Regex.Replace(slow.Name, @"\s*\(.*\)$", "")}";

    /// <summary>Quantas classificações precisaram do modelo lento.</summary>
    public int SlowCalls { get; private set; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await fast.InitializeAsync(ct);
        await slow.InitializeAsync(ct);
    }

    public async Task<RiskResult> ClassifyAsync(string action, CancellationToken ct = default)
    {
        var hit = rules.TryMatch(action);
        if (hit != null) return hit with { Engine = Name };

        var quick = await fast.ClassifyAsync(action, ct);
        if (quick.Confidence >= threshold) return quick with { Engine = Name };

        SlowCalls++;
        var careful = await slow.ClassifyAsync(action, ct);
        return careful with { Engine = Name, Elapsed = quick.Elapsed + careful.Elapsed,
                              Reason = NxText.T($"ONNX em dúvida ({quick.Confidence:P0}) → {careful.Reason}", $"ONNX unsure ({quick.Confidence:P0}) → {careful.Reason}", $"ONNX con dudas ({quick.Confidence:P0}) → {careful.Reason}") };
    }

    public void Dispose()
    {
        fast.Dispose();
        slow.Dispose();
    }
}
