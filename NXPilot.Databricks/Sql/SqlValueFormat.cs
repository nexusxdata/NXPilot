// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.Text.RegularExpressions;

namespace NXPilot.Databricks;

/// <summary>Como apresentar o valor de uma coluna (pelo tipo do Databricks).</summary>
public enum SqlValueKind { Text, Integer, Decimal, Date, Timestamp, Boolean }

/// <summary>
/// Valores do Databricks (sempre texto na API) prontos para quem lê: números com separador de milhar
/// e as casas do tipo, datas dd/MM/yyyy (com hora só quando há hora), Sim/Não. A data/hora fica como
/// o Databricks gravou (sem converter fuso).
/// </summary>
public static class SqlValueFormat
{
    private static readonly Regex Scale = new(@"decimal\s*\(\s*\d+\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase);

    public static SqlValueKind KindOf(string? type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (t.StartsWith("tinyint") || t.StartsWith("smallint") || t.StartsWith("int") || t.StartsWith("bigint") || t.StartsWith("long") || t.StartsWith("short") || t.StartsWith("byte"))
            return SqlValueKind.Integer;
        if (t.StartsWith("decimal") || t.StartsWith("double") || t.StartsWith("float") || t.StartsWith("real") || t.StartsWith("numeric")) return SqlValueKind.Decimal;
        if (t == "date") return SqlValueKind.Date;
        if (t.StartsWith("timestamp")) return SqlValueKind.Timestamp;
        if (t.StartsWith("bool")) return SqlValueKind.Boolean;
        return SqlValueKind.Text;
    }

    /// <summary>Casas decimais do tipo: DECIMAL(p,s) → s; DOUBLE/FLOAT → até 4 (sem zeros à direita).</summary>
    public static int? DecimalPlaces(string? type) => Scale.Match(type ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>Valor tipado: long/double, DateTime, bool ou o próprio texto (nulo fica nulo).</summary>
    public static object? Parse(string? value, SqlValueKind kind)
    {
        if (value == null) return null;
        switch (kind)
        {
            case SqlValueKind.Integer when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l): return l;
            case SqlValueKind.Decimal when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d): return d;
            case SqlValueKind.Date or SqlValueKind.Timestamp when DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt): return dt;
            case SqlValueKind.Boolean when bool.TryParse(value, out var b): return b;
            default: return value;
        }
    }

    /// <summary>Texto para a tela, na cultura <paramref name="culture"/> (pt-BR: 1.234,56 · 15/09/2026).</summary>
    public static string Display(string? value, string? type, CultureInfo culture, string yes = "Sim", string no = "Não")
    {
        var kind = KindOf(type);
        return Parse(value, kind) switch
        {
            null => "",
            long l => Math.Abs(l) < 10_000 ? l.ToString(culture) : l.ToString("#,##0", culture), // ano/mês/código curto sem separador (2025, não 2.025)
            double d => DecimalPlaces(type) is { } s ? d.ToString("N" + s, culture) : d.ToString("#,##0.####", culture),
            DateTime dt => kind == SqlValueKind.Date || dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("dd/MM/yyyy", culture) : dt.ToString(dt.Second == 0 ? "dd/MM/yyyy HH:mm" : "dd/MM/yyyy HH:mm:ss", culture),
            bool b => b ? yes : no,
            var other => other.ToString() ?? "",
        };
    }

    /// <summary>Números e datas ficam à direita.</summary>
    public static bool AlignRight(SqlValueKind kind) => kind is SqlValueKind.Integer or SqlValueKind.Decimal or SqlValueKind.Date or SqlValueKind.Timestamp;
}
