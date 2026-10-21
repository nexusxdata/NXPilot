// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;
using System.Text.RegularExpressions;

namespace NXPilot.Agents;

public sealed record PlannedFile(string Path, string Change, string Why);

public sealed record PlannedPhase(int Number, string Title, IReadOnlyList<PlannedFile> Files);

/// <summary>Achado da verificação. Severity: "Critico", "Maior" ou "Menor" (mesmas chaves das cores da tela).</summary>
public sealed record Finding(string Severity, string File, int? Line, string Message);

/// <param name="ReviewerFailed">O revisor não respondeu (erro, modelo recusado, sem saída): não há achados para corrigir.</param>
public sealed record VerificationResult(bool Approved, string Summary, IReadOnlyList<Finding> Findings, string Reviewer, bool ReviewerFailed = false);

/// <summary>
/// Prompts e leitura das respostas estruturadas do plano (fases) e da verificação (achados).
/// O texto livre do agente é para a pessoa; o JSON é para o NXPilot — e a leitura é
/// tolerante: sem JSON válido, o plano vira uma fase única e a verificação vira um aviso.
/// </summary>
public static class TaskPlanning
{
    // ------------------------------------------------------------------ plano

    public static string PlanPrompt(string title, string intent, string criteria) => NxText.T(
        $"Tarefa: {title}\n\nIntenção:\n{intent}\n\nCritérios de aceite:\n{criteria}\n\n" +
        "Analise o repositório e proponha um plano de implementação objetivo, dividido em fases pequenas " +
        "(1 a 4 fases; cada fase deve deixar o projeto funcionando). Não altere nada agora.\n\n" +
        "Escreva o plano para uma pessoa ler e, NO FINAL, inclua um bloco ```json exatamente neste formato:\n" +
        "{\"phases\":[{\"title\":\"...\",\"files\":[{\"path\":\"caminho/relativo\",\"change\":\"new|modify|delete\",\"why\":\"...\"}]}]}\n\n" +
        "Se NÃO conseguir propor um plano confiável (faltam dados, uma ferramenta ou acesso), não invente fases: explique o motivo e termine com " +
        "```json {\"phases\":[],\"blocked\":\"o motivo, em uma frase\"} ```",
        $"Task: {title}\n\nIntent:\n{intent}\n\nAcceptance criteria:\n{criteria}\n\n" +
        "Analyze the repository and propose a concise implementation plan, split into small phases " +
        "(1 to 4 phases; each phase must leave the project working). Do not change anything now.\n\n" +
        "Write the plan for a person to read and, AT THE END, include a ```json block exactly in this format:\n" +
        "{\"phases\":[{\"title\":\"...\",\"files\":[{\"path\":\"relative/path\",\"change\":\"new|modify|delete\",\"why\":\"...\"}]}]}\n\n" +
        "If you CANNOT propose a reliable plan (missing data, a tool or access), do not invent phases: explain why and end with " +
        "```json {\"phases\":[],\"blocked\":\"the reason, in one sentence\"} ```",
        $"Tarea: {title}\n\nIntención:\n{intent}\n\nCriterios de aceptación:\n{criteria}\n\n" +
        "Analice el repositorio y proponga un plan de implementación objetivo, dividido en fases pequeñas " +
        "(de 1 a 4 fases; cada fase debe dejar el proyecto funcionando). No modifique nada ahora.\n\n" +
        "Escriba el plan para que una persona lo lea y, AL FINAL, incluya un bloque ```json exactamente en este formato:\n" +
        "{\"phases\":[{\"title\":\"...\",\"files\":[{\"path\":\"ruta/relativa\",\"change\":\"new|modify|delete\",\"why\":\"...\"}]}]}\n\n" +
        "Si NO puede proponer un plan confiable (faltan datos, una herramienta o acceso), no invente fases: explique el motivo y termine con " +
        "```json {\"phases\":[],\"blocked\":\"el motivo, en una frase\"} ```");

    public static string PhasePrompt(PlannedPhase phase, int total) => NxText.T(
        $"Plano aprovado. Execute agora SOMENTE a Fase {phase.Number} de {total}: {phase.Title}. " +
        "Trabalhe só dentro deste diretório e não avance para a próxima fase. Ao terminar, resuma o que mudou.",
        $"Plan approved. Execute ONLY Phase {phase.Number} of {total} now: {phase.Title}. " +
        "Work only inside this directory and do not move on to the next phase. When done, summarize what changed.",
        $"Plan aprobado. Ejecute ahora SOLO la Fase {phase.Number} de {total}: {phase.Title}. " +
        "Trabaje solo dentro de este directorio y no avance a la siguiente fase. Al terminar, resuma lo que cambió.");

    /// <summary>
    /// "Analisar e corrigir achados": devolve ao executor o que o revisor apontou. Ele confere cada
    /// achado, corrige o que procede e explica o que não procede — sem refazer o resto da tarefa.
    /// </summary>
    public static string FixFindingsPrompt(VerificationResult v)
    {
        var list = string.Join("\n", v.Findings.Select(f => $"- [{f.Severity}] {f.File}{(f.Line is { } l ? $":{l}" : "")} — {f.Message}"));
        return NxText.T(
            $"A verificação cruzada ({v.Reviewer}) NÃO aprovou o resultado. Resumo: {v.Summary}\nAchados:\n{list}\n\n" +
            "Analise cada achado no código deste diretório. Corrija os que procedem, com a menor mudança possível, e confira que a correção funciona. " +
            "Se algum não proceder, não mude nada por causa dele e explique por quê. Não refaça o resto da tarefa. Ao terminar, resuma achado por achado o que fez.",
            $"Cross-verification ({v.Reviewer}) did NOT approve the result. Summary: {v.Summary}\nFindings:\n{list}\n\n" +
            "Analyze each finding in the code in this directory. Fix the valid ones with the smallest possible change, and check that the fix works. " +
            "If a finding is not valid, change nothing because of it and explain why. Do not redo the rest of the task. When done, summarize finding by finding what you did.",
            $"La verificación cruzada ({v.Reviewer}) NO aprobó el resultado. Resumen: {v.Summary}\nHallazgos:\n{list}\n\n" +
            "Analice cada hallazgo en el código de este directorio. Corrija los que proceden, con el menor cambio posible, y verifique que la corrección funciona. " +
            "Si alguno no procede, no cambie nada por él y explique por qué. No rehaga el resto de la tarea. Al terminar, resuma hallazgo por hallazgo lo que hizo.");
    }

    /// <summary>
    /// O agente NÃO entregou um plano executável: disse que está bloqueado ("blocked"), mandou
    /// fases vazias ou não mandou o bloco de fases. Devolve o motivo (para a tela); nulo = plano ok.
    /// Nesse caso a tarefa não deve ir para "aprovar e executar".
    /// </summary>
    public static string? PlanBlockedReason(string planText)
    {
        var json = LastJsonBlock(planText);
        if (json == null)
            return NxText.T("o agente não devolveu as fases do plano", "the agent did not return the plan phases", "el agente no devolvió las fases del plan");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } reason) return reason;
            if (!root.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array || phases.GetArrayLength() == 0)
                return NxText.T("o plano veio sem fases", "the plan came without phases", "el plan vino sin fases");
            return null;
        }
        catch (JsonException)
        {
            return NxText.T("as fases do plano vieram em formato inválido", "the plan phases came in an invalid format", "las fases del plan vinieron en un formato inválido");
        }
    }

    /// <summary>Fases do bloco ```json no fim do plano; sem bloco válido, uma fase única.</summary>
    public static IReadOnlyList<PlannedPhase> ParsePhases(string planText)
    {
        try
        {
            if (LastJsonBlock(planText) is { } json)
            {
                using var doc = JsonDocument.Parse(json);
                var phases = new List<PlannedPhase>();
                foreach (var p in doc.RootElement.GetProperty("phases").EnumerateArray())
                {
                    var files = new List<PlannedFile>();
                    if (p.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
                        foreach (var f in fs.EnumerateArray())
                            files.Add(new PlannedFile(Str(f, "path") ?? "?", Str(f, "change") ?? "modify", Str(f, "why") ?? ""));
                    phases.Add(new PlannedPhase(phases.Count + 1, Str(p, "title") ?? NxText.T($"Fase {phases.Count + 1}", $"Phase {phases.Count + 1}", $"Fase {phases.Count + 1}"), files));
                }
                if (phases.Count > 0) return phases;
            }
        }
        catch (Exception) { /* JSON fora do formato: cai na fase única */ }
        return new[] { new PlannedPhase(1, NxText.T("Executar o plano", "Execute the plan", "Ejecutar el plan"), Array.Empty<PlannedFile>()) };
    }

    /// <summary>O plano para a pessoa, sem o bloco JSON técnico do final.</summary>
    public static string HumanPlan(string planText)
    {
        var i = planText.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        return (i >= 0 ? planText[..i] : planText).TrimEnd();
    }

    // ------------------------------------------------------------------ verificação

    /// <summary>Esquema para o --output-schema do Codex (todas as chaves obrigatórias, sem extras).</summary>
    public const string VerificationSchema = """
    {"type":"object","additionalProperties":false,"required":["approved","summary","findings"],
     "properties":{
       "approved":{"type":"boolean"},
       "summary":{"type":"string"},
       "findings":{"type":"array","items":{"type":"object","additionalProperties":false,
         "required":["severity","file","line","message"],
         "properties":{
           "severity":{"type":"string","enum":["critical","major","minor"]},
           "file":{"type":"string"},
           "line":{"type":["integer","null"]},
           "message":{"type":"string"}}}}}}
    """;

    /// <summary>
    /// Pedido ao revisor: diff + plano + critérios e, quando houver, as ações que a tarefa fez FORA do
    /// repositório pelo MCP NXPilot (Databricks, DevOps, Power BI…) — o diff não mostra isso, e sem a
    /// lista o revisor acusaria como "não feito" o que foi feito.
    /// </summary>
    public static string VerificationPrompt(string title, string criteria, string plan, string diff, IReadOnlyList<string>? externalActions = null,
        string? previousRounds = null)
    {
        var actions = externalActions is { Count: > 0 } ? string.Join("\n", externalActions.Select(a => "- " + a)) : null;
        // Rodadas anteriores: o que foi achado e como o executor respondeu (inclusive "não procede", com evidência).
        var rounds = string.IsNullOrWhiteSpace(previousRounds) ? "" : NxText.T(
            "\n\nRODADAS ANTERIORES (achados e a resposta do executor). Se o executor contestou um achado com evidência, CONFIRA a evidência (MCP, somente leitura) antes de repetir: " +
            "repita o achado só se ele continuar valendo, com o motivo novo.\n" + previousRounds,
            "\n\nPREVIOUS ROUNDS (findings and the executor's answer). If the executor disputed a finding with evidence, CHECK the evidence (MCP, read-only) before repeating it: " +
            "repeat the finding only if it still holds, with the new reason.\n" + previousRounds,
            "\n\nRONDAS ANTERIORES (hallazgos y la respuesta del ejecutor). Si el ejecutor refutó un hallazgo con evidencia, VERIFIQUE la evidencia (MCP, solo lectura) antes de repetirlo: " +
            "repita el hallazgo solo si sigue valiendo, con el motivo nuevo.\n" + previousRounds);
        return NxText.T(
            "Você é o REVISOR de uma mudança feita por outro agente de código. Não altere nada.\n" +
            "Compare o diff com o plano aprovado e com os critérios de aceite. Aponte erros, riscos, coisas fora do plano e critérios não atendidos.\n" +
            "O diff só mostra os ARQUIVOS do repositório. O que a tarefa fez em sistemas externos (Databricks, DevOps, Power BI, e-mail…) está em AÇÕES FORA DO REPOSITÓRIO: considere feito o que está lá. " +
            "Se você tiver as ferramentas MCP do NXPilot (somente leitura), confira antes de dizer que algo não existe (ex.: databricks_sql com SHOW, DESCRIBE ou SELECT). " +
            "Nunca aponte como faltando algo só porque não aparece no diff.\n" +
            "Gravidade: critical = quebra, perda de dados, segurança ou critério de aceite não atendido; major = defeito real ou desvio relevante do plano; minor = melhoria.\n" +
            "approved = true só se não houver achado critical. Escreva summary e message em português.\n" +
            "Responda SOMENTE com JSON: {\"approved\":bool,\"summary\":\"...\",\"findings\":[{\"severity\":\"critical|major|minor\",\"file\":\"...\",\"line\":número ou null,\"message\":\"...\"}]}\n\n" +
            $"TAREFA: {title}\n\nCRITÉRIOS DE ACEITE:\n{criteria}\n\nPLANO APROVADO:\n{Trim(plan, 6000)}\n\n" +
            $"AÇÕES FORA DO REPOSITÓRIO (executadas pelo MCP NXPilot, aprovadas pelo usuário):\n{actions ?? "(nenhuma)"}\n\nDIFF:\n{Trim(diff, 30000)}{rounds}",
            "You are the REVIEWER of a change made by another coding agent. Do not change anything.\n" +
            "Compare the diff with the approved plan and the acceptance criteria. Point out bugs, risks, off-plan changes and unmet criteria.\n" +
            "The diff only shows the repository FILES. What the task did in external systems (Databricks, DevOps, Power BI, e-mail…) is in ACTIONS OUTSIDE THE REPOSITORY: treat what is listed there as done. " +
            "If you have the NXPilot MCP tools (read-only), check before saying something does not exist (e.g. databricks_sql with SHOW, DESCRIBE or SELECT). " +
            "Never report something as missing just because it is not in the diff.\n" +
            "Severity: critical = breakage, data loss, security or unmet acceptance criterion; major = real defect or relevant deviation from the plan; minor = improvement.\n" +
            "approved = true only if there is no critical finding. Write summary and message in English.\n" +
            "Reply ONLY with JSON: {\"approved\":bool,\"summary\":\"...\",\"findings\":[{\"severity\":\"critical|major|minor\",\"file\":\"...\",\"line\":number or null,\"message\":\"...\"}]}\n\n" +
            $"TASK: {title}\n\nACCEPTANCE CRITERIA:\n{criteria}\n\nAPPROVED PLAN:\n{Trim(plan, 6000)}\n\n" +
            $"ACTIONS OUTSIDE THE REPOSITORY (run through the NXPilot MCP, approved by the user):\n{actions ?? "(none)"}\n\nDIFF:\n{Trim(diff, 30000)}{rounds}",
            "Usted es el REVISOR de un cambio hecho por otro agente de código. No altere nada.\n" +
            "Compare el diff con el plan aprobado y con los criterios de aceptación. Señale errores, riesgos, cambios fuera del plan y criterios no cumplidos.\n" +
            "El diff solo muestra los ARCHIVOS del repositorio. Lo que la tarea hizo en sistemas externos (Databricks, DevOps, Power BI, e-mail…) está en ACCIONES FUERA DEL REPOSITORIO: considere hecho lo que figura allí. " +
            "Si tiene las herramientas MCP de NXPilot (solo lectura), verifique antes de decir que algo no existe (p. ej., databricks_sql con SHOW, DESCRIBE o SELECT). " +
            "Nunca señale algo como faltante solo porque no aparece en el diff.\n" +
            "Gravedad: critical = rotura, pérdida de datos, seguridad o criterio de aceptación no cumplido; major = defecto real o desviación relevante del plan; minor = mejora.\n" +
            "approved = true solo si no hay ningún hallazgo critical. Escriba summary y message en español.\n" +
            "Responda SOLO con JSON: {\"approved\":bool,\"summary\":\"...\",\"findings\":[{\"severity\":\"critical|major|minor\",\"file\":\"...\",\"line\":número o null,\"message\":\"...\"}]}\n\n" +
            $"TAREA: {title}\n\nCRITERIOS DE ACEPTACIÓN:\n{criteria}\n\nPLAN APROBADO:\n{Trim(plan, 6000)}\n\n" +
            $"ACCIONES FUERA DEL REPOSITORIO (ejecutadas por el MCP de NXPilot, aprobadas por el usuario):\n{actions ?? "(ninguna)"}\n\nDIFF:\n{Trim(diff, 30000)}{rounds}");
    }

    public static VerificationResult ParseVerification(string text, string reviewer)
    {
        try
        {
            var json = LastJsonBlock(text) ?? FirstJsonObject(text);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var findings = new List<Finding>();
                if (root.TryGetProperty("findings", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    foreach (var f in fs.EnumerateArray())
                        findings.Add(new Finding(
                            (Str(f, "severity") ?? "minor") switch { "critical" => "Critico", "major" => "Maior", _ => "Menor" },
                            Str(f, "file") ?? "",
                            f.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
                            Str(f, "message") ?? ""));
                var approved = root.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True
                               && findings.All(x => x.Severity != "Critico");
                return new VerificationResult(approved, Str(root, "summary") ?? "", findings, reviewer);
            }
        }
        catch (Exception) { /* resposta fora do formato */ }
        // Sem resposta nenhuma: o revisor falhou (erro já está no Log/Terminal) — não é um achado do código.
        if (string.IsNullOrWhiteSpace(text))
            return new VerificationResult(false, NxText.T("O revisor não respondeu (veja o erro no Log da verificação). Nada foi revisado — rode de novo ou troque o revisor/modelo.",
                "The reviewer did not answer (see the error in the verification Log). Nothing was reviewed — run again or change the reviewer/model.",
                "El revisor no respondió (vea el error en el Log de la verificación). Nada fue revisado — ejecute de nuevo o cambie el revisor/modelo."),
                Array.Empty<Finding>(), reviewer, ReviewerFailed: true);
        return new VerificationResult(false, NxText.T("O revisor não devolveu o JSON esperado; leia a resposta no Terminal.", "The reviewer did not return the expected JSON; read the answer in the Terminal.", "El revisor no devolvió el JSON esperado; lea la respuesta en la Terminal."),
            new[] { new Finding("Maior", "", null, Trim(text, 400)) }, reviewer);
    }

    private static string? LastJsonBlock(string text)
    {
        var matches = Regex.Matches(text, "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    private static string? FirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "\n…" : s;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
