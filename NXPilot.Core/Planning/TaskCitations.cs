// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.RegularExpressions;

namespace NXPilot.Planning;

/// <summary>
/// Arquivo/pasta citado na tarefa. No texto fica só o marcador, que já diz o tipo:
/// [xlsx01], [docx02], [folder01], [file01] (sem extensão).
/// </summary>
/// <param name="Kind">Extensão em minúsculas ("xlsx"), "folder" ou "file".</param>
/// <param name="Number">Numeração própria de cada tipo (xlsx01, xlsx02…).</param>
/// <param name="Display">O que o agente usa: relativo ao assunto/repositório ("relatorios/q2.xlsx") ou o caminho completo.</param>
public sealed record Citation(string Kind, int Number, string Display, string FullPath)
{
    public string Marker => $"[{Kind}{Number:00}]";
}

/// <summary>
/// Citações da Nova tarefa, como as referências de um artigo: o texto leva os marcadores e a
/// legenda ("[xlsx01] `relatorios/q2.xlsx`") vai no fim da intenção para o agente. Os colchetes
/// evitam confundir com "#1253030" de work item/issue.
/// </summary>
public static class TaskCitations
{
    public const string FolderKind = "folder", FileKind = "file";

    // Os 3 primeiros são os de hoje; os outros, de tarefas antigas (clonar reconhece as duas).
    private static readonly string[] Headers = ["Citados (arquivos, pastas e work items):", "Cited (files, folders and work items):", "Citados (archivos, carpetas y work items):",
        "Arquivos e pastas citados:", "Cited files and folders:", "Archivos y carpetas citados:"];

    /// <summary>Marca da legenda que diz ao agente como ler o work item (e reconhece a citação de work item).</summary>
    public const string WorkItemToolHint = "workitems_tree id=";

    /// <summary>
    /// Work item citado como um anexo: [story01], [task01], [epic01], [feature01], [project01], [bug01].
    /// Na legenda vai o tipo, o id, o título e como ler (workitems_tree id=… project="…").
    /// </summary>
    public static Citation ForWorkItem(IEnumerable<Citation> existing, string type, int id, string title, string? project, string url)
    {
        var kind = WorkItemKind(type);
        var clean = title.Replace("`", "'").Replace("\r", " ").Replace("\n", " ").Trim();
        var display = project is { Length: > 0 }
            ? $"{type} #{id} · {clean} — {WorkItemToolHint}{id} project=\"{project}\""
            : $"{type} #{id} · {clean}";
        return new Citation(kind, NextNumber(existing, kind), display, url);
    }

    /// <summary>Tipo do work item → tipo do marcador ("User Story" → story, "EPIC_GROUP" → epicgroup).</summary>
    public static string WorkItemKind(string type)
    {
        var t = type.Trim().ToLowerInvariant();
        if (t is "user story" or "product backlog item" or "requirement" or "story") return "story";
        var slug = new string(t.Where(char.IsAsciiLetterOrDigit).ToArray());
        return slug.Length is > 0 and <= 12 ? slug : "workitem";
    }

    public static bool IsWorkItem(Citation c) => c.Display.Contains(WorkItemToolHint, StringComparison.Ordinal);

    // Tipo + 2 dígitos no fim ("mp301" = mp3 nº 01).
    private static readonly Regex LegendLine = new(@"^\[([a-z0-9]+)(\d{2})\] `([^`]+)`$");

    /// <summary>Tipo do marcador: "folder" para pasta; senão a extensão (só letras e dígitos, até 8) ou "file".</summary>
    public static string KindOf(string path, bool isFolder)
    {
        if (isFolder) return FolderKind;
        var ext = new string(Path.GetExtension(path).TrimStart('.').ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return ext.Length is > 0 and <= 8 ? ext : FileKind;
    }

    /// <summary>Próximo número livre do tipo (xlsx01, xlsx02…).</summary>
    public static int NextNumber(IEnumerable<Citation> citations, string kind) =>
        citations.Where(c => c.Kind == kind).Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Legenda a acrescentar à intenção (vazia sem citações), agrupada por tipo.</summary>
    public static string Legend(IEnumerable<Citation> citations)
    {
        var list = citations.OrderBy(c => c.Kind == FolderKind ? 1 : 0).ThenBy(c => c.Kind).ThenBy(c => c.Number).ToList();
        if (list.Count == 0) return "";
        return "\n\n" + NxText.T(Headers[0], Headers[1], Headers[2]) + "\n" + string.Join("\n", list.Select(c => $"{c.Marker} `{c.Display}`"));
    }

    /// <summary>
    /// Separa a legenda do fim da intenção (tarefa clonada do Histórico): devolve o texto sem ela e
    /// as citações. Sem legenda reconhecível, devolve o texto como veio.
    /// </summary>
    public static (string Text, List<Citation> Citations) SplitLegend(string intent)
    {
        var lines = intent.Replace("\r\n", "\n").Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!Headers.Contains(lines[i].Trim())) continue;
            var citations = new List<Citation>();
            foreach (var line in lines.Skip(i + 1).Where(l => l.Trim().Length > 0))
            {
                var m = LegendLine.Match(line.Trim());
                if (!m.Success) return (intent, new()); // não é a legenda do NXPilot: deixa o texto como está
                citations.Add(new Citation(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value, m.Groups[3].Value));
            }
            return (string.Join("\n", lines.Take(i)).TrimEnd(), citations);
        }
        return (intent, new());
    }

    /// <summary>
    /// Tira os marcadores da citação (com a vírgula que os separava de outro marcador) e renumera
    /// as seguintes do MESMO tipo: tirando [xlsx01], o [xlsx02] vira [xlsx01]; [docx01] fica.
    /// </summary>
    public static string RemoveAndRenumber(string text, Citation removed)
    {
        var marker = Regex.Escape(removed.Marker);
        text = Regex.Replace(text, $@"{marker}, ", "");          // "[a01], [b01]" → "[b01]"
        text = Regex.Replace(text, $@", {marker}", "");          // "[b01], [a01]" → "[b01]"
        text = Regex.Replace(text, $@" ?{marker}", "");          // marcador sozinho, com o espaço antes
        return Regex.Replace(text, $@"\[{Regex.Escape(removed.Kind)}(\d{{2}})\]", m =>
        {
            var n = int.Parse(m.Groups[1].Value);
            return n > removed.Number ? $"[{removed.Kind}{n - 1:00}]" : m.Value;
        });
    }

    /// <summary>
    /// Orientação ao agente quando a tarefa cita work items: de onde vêm os dados, como ler a referência e,
    /// ao criar itens, horas = estimativa original e aviso quando a referência cresceu. Usada pelo app e pelo Bench.
    /// </summary>
    public static string WorkItemsGuidance() => NxText.T(
            "Os work items citados ([story01], [task01], [epic01]…) vêm do Azure DevOps, não do repositório: leia cada um — e o que está abaixo dele — com a ferramenta MCP workitems_tree (id e project na legenda acima) antes de planejar. Item de referência (ex.: uma story encerrada usada como modelo): leia TUDO, sem open_only — as tasks fechadas dele são o modelo. open_only=true só para ver o que falta no item a trabalhar. Se a tarefa for criar ou organizar work items (ex.: criar as tasks de uma story com base em outra), o entregável são os itens no DevOps/GitHub, não código: o plano é uma fase que cria os itens com a ferramenta MCP nxproject_create_items (com \"files\": []) — confira antes com nxproject_status se o usuário pode gravar ali. No plano, proponha a lista (tipo, título, horas, responsável) — as horas das tasks novas são a estimativa ORIGINAL da referência, não as horas trabalhadas. Na referência, compare a estimativa original com a atual (HH Atual) que o workitems_tree mostra: se ela cresceu (tasks que entraram depois do plano original), avise no plano com os números e liste essas tasks com as horas, para o usuário decidir se entram — nunca as descarte em silêncio. O repositório pode estar vazio.",
            "The cited work items ([story01], [task01], [epic01]…) come from Azure DevOps, not from the repository: read each one — and what is below it — with the MCP tool workitems_tree (id and project in the legend above) before planning. A reference item (e.g. a closed story used as a model): read EVERYTHING, without open_only — its closed tasks are the model. open_only=true only to see what is missing in the item to work on. If the task is to create or organize work items (e.g. create a story's tasks based on another), the deliverable is the items in DevOps/GitHub, not code: the plan is a phase that creates them with the MCP tool nxproject_create_items (with \"files\": []) — check first with nxproject_status whether the user may write there. In the plan, propose the list (kind, title, hours, assignee) — new tasks take the reference's ORIGINAL estimate, not the completed hours. For a reference item, compare the original estimate with the current one (HH Atual) shown by workitems_tree: if it grew (tasks added after the original plan), warn in the plan with the numbers and list those tasks with their hours, so the user decides whether they go in — never drop them silently. The repository may be empty.",
            "Los work items citados ([story01], [task01], [epic01]…) vienen de Azure DevOps, no del repositorio: lea cada uno — y lo que está debajo — con la herramienta MCP workitems_tree (id y project en la leyenda de arriba) antes de planificar. Elemento de referencia (p. ej., una story cerrada usada como modelo): lea TODO, sin open_only — sus tasks cerradas son el modelo. open_only=true solo para ver lo que falta en el elemento a trabajar. Si la tarea es crear u organizar work items (p. ej., crear las tasks de una story con base en otra), el entregable son los elementos en DevOps/GitHub, no código: el plan es una fase que los crea con la herramienta MCP nxproject_create_items (con \"files\": []) — verifique antes con nxproject_status si el usuario puede escribir allí. En el plan, proponga la lista (tipo, título, horas, responsable) — las tasks nuevas llevan la estimación ORIGINAL de la referencia, no las horas trabajadas. En la referencia, compare la estimación original con la actual (HH Atual) que muestra workitems_tree: si creció (tasks agregadas después del plan original), avise en el plan con los números y liste esas tasks con sus horas, para que el usuario decida si entran — nunca las descarte en silencio. El repositorio puede estar vacío.");
}
