// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.RegularExpressions;

namespace NXPilot.Databricks;

/// <summary>
/// Consulta de amostra da tela "Consultar dados" da extensão Databricks: só leitura, uma instrução,
/// e no máximo <see cref="MaxRows"/> registros — o LIMIT é acrescentado (ou reduzido) sempre.
/// </summary>
public static class SqlPreview
{
    public const int MaxRows = 50;

    private static readonly Regex TrailingLimit = new(@"\bLIMIT\s+(\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Limitable = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "VALUES", "TABLE", "FROM" };

    /// <summary>
    /// O SQL que será executado, ou o motivo de não executar. SELECT/WITH/VALUES/TABLE/FROM recebem
    /// "LIMIT 50" (um LIMIT maior no fim vira 50); SHOW/DESCRIBE rodam como estão (o resultado é cortado em 50).
    /// </summary>
    public static (string? Sql, string? Error) Prepare(string sql)
    {
        var text = (sql ?? "").Trim();
        while (text.EndsWith(';')) text = text[..^1].TrimEnd();
        if (text.Length == 0) return (null, "Escreva uma consulta.");
        var review = SqlStatementClassifier.Classify(text);
        if (review.Statements.Count > 1) return (null, "Uma consulta por vez.");
        if (!review.IsReadOnly) return (null, $"Só consultas de leitura nesta tela ({review.Kind}).");

        var code = WithoutTrailingComments(text);
        var first = Regex.Match(code, @"^\(*\s*([A-Za-z]+)").Groups[1].Value;
        if (!Limitable.Contains(first)) return (code, null);
        if (TrailingLimit.Match(code) is { Success: true } m)
            return int.TryParse(m.Groups[1].Value, out var n) && n <= MaxRows ? (code, null)
                : (code[..m.Index] + $"LIMIT {MaxRows}", null);
        return (code + $"\nLIMIT {MaxRows}", null);
    }

    /// <summary>Tira comentários "--" e "/* */" do FIM (senão o LIMIT acrescentado ficaria comentado).</summary>
    private static string WithoutTrailingComments(string sql)
    {
        var s = sql.TrimEnd();
        while (true)
        {
            var lastLine = s[(s.LastIndexOf('\n') + 1)..];
            var dash = IndexOfLineComment(lastLine);
            if (dash >= 0) { s = (s[..(s.Length - lastLine.Length)] + lastLine[..dash]).TrimEnd(); continue; }
            if (s.EndsWith("*/") && s.LastIndexOf("/*", StringComparison.Ordinal) is var open and >= 0) { s = s[..open].TrimEnd(); continue; }
            return s;
        }
    }

    /// <summary>Posição de "--" fora de texto entre aspas na linha (ou -1).</summary>
    private static int IndexOfLineComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') return i;
        }
        return -1;
    }
}
