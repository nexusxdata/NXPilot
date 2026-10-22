// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.RegularExpressions;

namespace NXPilot.Risk;

/// <summary>
/// O pedido de aprovação em uma frase para quem não é técnico: o que o agente quer fazer e por que o risco
/// é esse nível ("O agente quer apagar arquivos no seu computador. O risco é alto porque o que é apagado não volta.").
/// Só regras (milissegundos, sem IA) — o comando técnico continua no cartão para quem quiser ler.
/// </summary>
public static class PlainRisk
{
    private sealed record Kind(Regex Match, string[] What, string[] Why);

    private static Kind K(string pattern, string[] what, string[] why) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), what, why);

    // A ordem importa: o primeiro que casar explica (apagar vem antes de "rodar um programa").
    private static readonly Kind[] Commands =
    [
        K(@"\b(rm|rmdir|del|erase|Remove-Item|rd|git\s+clean|git\s+reset\s+--hard|DROP\s|TRUNCATE\s|DELETE\s+FROM)\b",
            ["apagar arquivos ou dados", "delete files or data", "borrar archivos o datos"],
            ["o que é apagado pode não voltar", "what is deleted may not come back", "lo que se borra puede no volver"]),
        K(@"\bgit\s+push\b",
            ["enviar o código para o servidor (GitHub/DevOps)", "send the code to the server (GitHub/DevOps)", "enviar el código al servidor (GitHub/DevOps)"],
            ["o que vai para o servidor fica visível para outras pessoas", "what goes to the server becomes visible to other people", "lo que va al servidor queda visible para otras personas"]),
        K(@"\b(kubectl|az\s|aws\s|gcloud|terraform)\b",
            ["mexer em um serviço na nuvem", "change a cloud service", "modificar un servicio en la nube"],
            ["o serviço pode ser usado por outras pessoas", "the service may be used by other people", "el servicio puede ser usado por otras personas"]),
        K(@"\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr|irm|scp|ftp)\b",
            ["baixar ou enviar dados pela internet", "download or send data over the internet", "descargar o enviar datos por internet"],
            ["dados podem entrar ou sair do seu computador", "data can come into or leave your computer", "los datos pueden entrar o salir de su computadora"]),
        K(@"\b(npm\s+(i|install|add)|pip\s+install|dotnet\s+add|winget|choco|apt(-get)?\s+install|Install-Module|Install-Package)\b",
            ["instalar um programa ou biblioteca", "install a program or library", "instalar un programa o biblioteca"],
            ["um programa novo pode mudar o seu ambiente", "a new program can change your environment", "un programa nuevo puede cambiar su entorno"]),
        K(@"\b(setx|reg\s+(add|delete)|Set-ItemProperty|New-ItemProperty|\[Environment\]::SetEnvironmentVariable)\b",
            ["mudar configurações do Windows", "change Windows settings", "cambiar la configuración de Windows"],
            ["a mudança vale para outros programas também", "the change affects other programs too", "el cambio también afecta a otros programas"]),
        K(@"\bgit\s+(commit|add|checkout|switch|merge|rebase|stash)\b",
            ["salvar ou trocar a versão do trabalho (git)", "save or switch the version of the work (git)", "guardar o cambiar la versión del trabajo (git)"],
            ["fica na pasta da tarefa e dá para voltar", "it stays in the task folder and can be undone", "queda en la carpeta de la tarea y se puede deshacer"]),
        K(@"\bgit\s+(status|log|diff|show|branch|rev-parse|ls-files)\b",
            ["consultar o histórico de versões do projeto", "look at the project's version history", "consultar el historial de versiones del proyecto"],
            ["só lê, não muda nada", "it only reads, nothing changes", "solo lee, no cambia nada"]),
        K(@"\b(Get-ChildItem|Get-Content|Get-Location|Select-String|Test-Path|ls|dir|cat|type|rg|grep|find|head|tail|pwd|echo|Write-Output)\b",
            ["ler ou listar arquivos", "read or list files", "leer o listar archivos"],
            ["só lê, não muda nada", "it only reads, nothing changes", "solo lee, no cambia nada"]),
        K(@"\b(python|py|node|dotnet|pwsh|powershell|cmd|bash|java|npm\s+run|npx)\b|\.(ps1|py|js|bat|cmd|sh)\b",
            ["rodar um programa ou script", "run a program or script", "ejecutar un programa o script"],
            ["o programa faz o que estiver escrito nele (use \"Ver script\" se aparecer)", "the program does whatever is written in it (use \"View script\" if shown)", "el programa hace lo que esté escrito en él (use \"Ver script\" si aparece)"]),
    ];

    // Ferramentas do MCP NXPilot, pelo prefixo do nome.
    private static readonly (string Prefix, string[] What)[] Tools =
    [
        ("pptx_", ["trabalhar na apresentação de PowerPoint", "work on the PowerPoint presentation", "trabajar en la presentación de PowerPoint"]),
        ("xlsx_", ["trabalhar na planilha de Excel", "work on the Excel spreadsheet", "trabajar en la hoja de Excel"]),
        ("powerbi_", ["trabalhar no relatório do Power BI", "work on the Power BI report", "trabajar en el informe de Power BI"]),
        ("databricks_sql", ["rodar uma consulta no Databricks", "run a query on Databricks", "ejecutar una consulta en Databricks"]),
        ("databricks_", ["consultar o Databricks", "look up Databricks", "consultar Databricks"]),
        ("mail_", ["ler os seus e-mails", "read your e-mails", "leer sus correos"]),
        ("teams_", ["enviar uma mensagem no Teams", "send a Teams message", "enviar un mensaje en Teams"]),
        ("workitems_", ["consultar ou alterar itens de trabalho (DevOps)", "look up or change work items (DevOps)", "consultar o cambiar elementos de trabajo (DevOps)"]),
        ("git", ["mexer em um repositório de código", "work with a code repository", "trabajar con un repositorio de código"]),
        ("windows_", ["ler janelas abertas no seu computador", "read open windows on your computer", "leer ventanas abiertas en su computadora"]),
    ];

    /// <summary>
    /// Uma frase simples para o pedido; comando que as regras não reconhecem fica só com o "o que" (sem nível nem "porquê").
    /// <paramref name="worktree"/> diz o que é "a pasta da tarefa" (arquivo fora dela pesa mais).
    /// </summary>
    public static string Explain(string action, RiskLevel level, string? worktree = null)
    {
        var (tool, body) = Split(action);
        string[] what;
        string[]? why;

        if (tool.StartsWith("MCP", StringComparison.OrdinalIgnoreCase) || tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            var name = Regex.Match(tool, @"(?:MCP\s+\w+\s+|mcp__\w+__)(\w+)").Groups[1].Value;
            what = Tools.FirstOrDefault(t => name.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)).What
                   ?? [$"usar a ferramenta \"{name}\" do NXPilot", $"use the NXPilot tool \"{name}\"", $"usar la herramienta \"{name}\" de NXPilot"];
            why = level == RiskLevel.Baixo
                ? ["só consulta ou mexe na pasta da tarefa", "it only reads or works in the task folder", "solo consulta o trabaja en la carpeta de la tarea"]
                : ["pode gravar dados fora da pasta da tarefa", "it may write data outside the task folder", "puede grabar datos fuera de la carpeta de la tarea"];
        }
        else if (tool.StartsWith("Databricks", StringComparison.OrdinalIgnoreCase))
        {
            what = ["rodar uma consulta no Databricks", "run a query on Databricks", "ejecutar una consulta en Databricks"];
            why = Commands[0].Match.IsMatch(body) ? Commands[0].Why
                : level == RiskLevel.Baixo ? ["só lê dados, não muda nada", "it only reads data, nothing changes", "solo lee datos, no cambia nada"]
                : ["pode criar ou mudar tabelas", "it may create or change tables", "puede crear o cambiar tablas"];
        }
        else if (tool is "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "apply_patch" or "fileChange")
        {
            var file = Path.GetFileName(body.Trim().Trim('"').Split(' ', '\n')[0]);
            var inside = worktree == null || !Path.IsPathRooted(body.Trim().Trim('"'))
                         || body.Trim().Trim('"').StartsWith(worktree, StringComparison.OrdinalIgnoreCase);
            what = file.Length > 0
                ? [$"alterar o arquivo \"{file}\"", $"change the file \"{file}\"", $"cambiar el archivo \"{file}\""]
                : ["alterar um arquivo", "change a file", "cambiar un archivo"];
            why = inside
                ? ["fica na pasta da tarefa e a versão anterior é guardada", "it stays in the task folder and the previous version is kept", "queda en la carpeta de la tarea y se guarda la versión anterior"]
                : ["o arquivo está fora da pasta da tarefa", "the file is outside the task folder", "el archivo está fuera de la carpeta de la tarea"];
        }
        else if (tool is "Read" or "Glob" or "Grep" or "LS" or "ToolSearch")
        {
            what = ["ler arquivos", "read files", "leer archivos"];
            why = ["só lê, não muda nada", "it only reads, nothing changes", "solo lee, no cambia nada"];
        }
        else if (tool is "WebFetch" or "WebSearch")
        {
            what = ["abrir uma página na internet", "open a web page", "abrir una página de internet"];
            why = ["o pedido sai do seu computador", "the request leaves your computer", "la solicitud sale de su computadora"];
        }
        else
        {
            // Comando que as regras não reconhecem: diz só o que é, sem nível nem "porquê" (um "não sei" assusta mais do que ajuda).
            var kind = Commands.FirstOrDefault(k => k.Match.IsMatch(body));
            what = kind?.What ?? ["rodar um comando", "run a command", "ejecutar un comando"];
            why = kind?.Why;
        }

        var levelText = level switch
        {
            RiskLevel.Baixo => NxText.T("baixo", "low", "bajo"),
            RiskLevel.Medio => NxText.T("médio", "medium", "medio"),
            _ => NxText.T("alto", "high", "alto"),
        };
        // Sem regra para o comando: só o que ele é. O nível fica no selo do cartão (vem do classificador),
        // sem frase que explique um risco que as regras não sabem justificar.
        if (why == null)
            return NxText.T($"O agente quer {what[0]} no seu computador.",
                            $"The agent wants to {what[1]} on your computer.",
                            $"El agente quiere {what[2]} en su computadora.");
        return NxText.T($"O agente quer {what[0]} no seu computador. O risco é {levelText} porque {why[0]}.",
                        $"The agent wants to {what[1]} on your computer. The risk is {levelText} because {why[1]}.",
                        $"El agente quiere {what[2]} en su computadora. El riesgo es {levelText} porque {why[2]}.");
    }

    /// <summary>"Bash: git push" → ("Bash", "git push"); "MCP nxpilot pptx_read: {…}" → ("MCP nxpilot pptx_read", "{…}").</summary>
    private static (string Tool, string Body) Split(string action)
    {
        var i = action.IndexOf(':');
        return i <= 0 ? ("", action) : (action[..i].Trim(), action[(i + 1)..].Trim());
    }
}
