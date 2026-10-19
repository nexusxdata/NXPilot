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

    private static readonly string[] Headers = ["Arquivos e pastas citados:", "Cited files and folders:", "Archivos y carpetas citados:"];

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
}
