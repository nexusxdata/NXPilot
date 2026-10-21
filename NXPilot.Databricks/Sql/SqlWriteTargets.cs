// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text;
using System.Text.RegularExpressions;

namespace NXPilot.Databricks;

/// <summary>Objeto que uma instrução cria ou altera, já com catálogo e schema resolvidos (nulo = não deu para saber).</summary>
public sealed record SqlWriteTarget(string? Catalog, string? Schema, string Object, string Kind)
{
    public override string ToString() => string.Join('.', new[] { Catalog, Schema }.Where(p => p != null).Append(Kind is "SCHEMA" or "DATABASE" ? null : Object).Where(p => p != null));
}

/// <summary>
/// Schemas de gravação do Databricks: a escrita (CREATE, INSERT, MERGE, UPDATE, DELETE, DROP, ALTER…)
/// só é permitida nos schemas que casam com os padrões do usuário — ex.: "sandbox_*" (qualquer
/// catálogo) ou "dev.sandbox_*" (catálogo.schema). Assim o agente cria objetos na área de teste e
/// nunca em produção. Nome sem schema usa o catálogo/schema padrão da conexão.
/// </summary>
public sealed class WriteSchemaPolicy
{
    private readonly List<(Regex? Catalog, Regex Schema)> _patterns = new();
    public string? DefaultCatalog { get; }
    public string? DefaultSchema { get; }
    public string PatternsText { get; }

    public WriteSchemaPolicy(string? patterns, string? defaultCatalog, string? defaultSchema)
    {
        DefaultCatalog = Clean(defaultCatalog);
        DefaultSchema = Clean(defaultSchema);
        var list = (patterns ?? "").Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('`')).ToList();
        PatternsText = string.Join(", ", list);
        foreach (var p in list)
        {
            var dot = p.LastIndexOf('.');
            _patterns.Add(dot > 0 ? (Glob(p[..dot]), Glob(p[(dot + 1)..])) : (null, Glob(p)));
        }
    }

    /// <summary>Sem padrões: a política não se aplica (escrita segue o gate de sempre).</summary>
    public bool IsActive => _patterns.Count > 0;

    public bool Allows(string? catalog, string? schema) =>
        schema != null && _patterns.Any(p => p.Schema.IsMatch(schema) && (p.Catalog == null || (catalog != null && p.Catalog.IsMatch(catalog))));

    /// <summary>
    /// Resultado para uma instrução de escrita: todos os destinos dentro (Allowed), algum fora
    /// (Blocked, com o destino), ou destino desconhecido (Unknown — vai para o usuário decidir).
    /// </summary>
    public (WriteScope Scope, IReadOnlyList<SqlWriteTarget> Targets, string Detail) Evaluate(string sql)
    {
        var (targets, determined) = SqlWriteTargets.Find(sql, DefaultCatalog, DefaultSchema);
        var outside = targets.FirstOrDefault(t => !Allows(t.Catalog, t.Schema));
        if (outside != null && outside.Schema != null)
            return (WriteScope.Blocked, targets, $"grava em {outside} — fora dos schemas de gravação ({PatternsText})");
        if (!determined || targets.Any(t => t.Schema == null))
            return (WriteScope.Unknown, targets, "não deu para identificar o schema de destino");
        if (targets.Count == 0)
            return (WriteScope.Allowed, targets, "só objetos temporários da sessão");
        return (WriteScope.Allowed, targets, $"grava em {string.Join(", ", targets.Select(t => t.ToString()).Distinct())} (schema de gravação)");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('`');

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim('`')).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

public enum WriteScope { Allowed, Blocked, Unknown }

/// <summary>
/// Acha os objetos que um SQL de escrita/DDL atinge. Por regras (não é um parser completo): ignora
/// comentários e textos, entende `crase` e nomes de 1 a 3 partes. Quando não reconhece a forma da
/// instrução, diz que não determinou — e quem chama trata como desconhecido (pergunta).
/// </summary>
public static class SqlWriteTargets
{
    private const string Part = @"(?:`[^`]+`|[A-Za-z_][\w$]*)";
    private const string Name = Part + @"(?:\s*\.\s*" + Part + "){0,2}";

    private static readonly Regex[] TableForms =
    [
        new($@"^\s*INSERT\s+(?:INTO|OVERWRITE)\s+(?:TABLE\s+)?(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*UPDATE\s+(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*DELETE\s+FROM\s+(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*MERGE\s+(?:WITH\s+SCHEMA\s+EVOLUTION\s+)?INTO\s+(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*COPY\s+INTO\s+(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*TRUNCATE\s+TABLE\s+(?<n>{Name})", RegexOptions.IgnoreCase),
        new($@"^\s*(?:OPTIMIZE|VACUUM|REORG\s+TABLE|ANALYZE\s+TABLE|REFRESH\s+(?:TABLE\s+|FOREIGN\s+TABLE\s+)?|RESTORE\s+(?:TABLE\s+)?|MSCK\s+REPAIR\s+TABLE|FSCK\s+REPAIR\s+TABLE|CACHE\s+(?:LAZY\s+)?TABLE|UNCACHE\s+TABLE\s+(?:IF\s+EXISTS\s+)?|SYNC\s+TABLE)\s*(?<n>{Name})", RegexOptions.IgnoreCase),
    ];

    private static readonly Regex Create = new(
        $@"^\s*(?:CREATE(?:\s+OR\s+(?:REPLACE|REFRESH))?|REPLACE)\s+(?<mods>(?:(?:GLOBAL\s+)?TEMP(?:ORARY)?\s+|EXTERNAL\s+|MATERIALIZED\s+|STREAMING\s+|LIVE\s+)*)(?<k>TABLE|VIEW|FUNCTION|VOLUME|SCHEMA|DATABASE|CATALOG)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<n>{Name})",
        RegexOptions.IgnoreCase);

    private static readonly Regex AlterDrop = new(
        $@"^\s*(?:ALTER|DROP|UNDROP)\s+(?:MATERIALIZED\s+|STREAMING\s+|TEMP(?:ORARY)?\s+)?(?<k>TABLE|VIEW|FUNCTION|VOLUME|SCHEMA|DATABASE|CATALOG)\s+(?:IF\s+EXISTS\s+)?(?<n>{Name})",
        RegexOptions.IgnoreCase);

    private static readonly Regex Comment = new($@"^\s*COMMENT\s+ON\s+(?<k>TABLE|SCHEMA|DATABASE|CATALOG|VOLUME|COLUMN)\s+(?<n>{Name})", RegexOptions.IgnoreCase);

    /// <summary>Escrita dentro de WITH … (INSERT/MERGE/UPDATE/DELETE depois das CTEs).</summary>
    private static readonly Regex InsideWith = new($@"\b(?:INSERT\s+(?:INTO|OVERWRITE)\s+(?:TABLE\s+)?|MERGE\s+INTO\s+|DELETE\s+FROM\s+|UPDATE\s+)(?<n>{Name})", RegexOptions.IgnoreCase);

    private static readonly Regex SessionChange = new(@"^\s*(?:USE|SET\s+(?:CATALOG|SCHEMA|DATABASE))\b", RegexOptions.IgnoreCase);

    /// <summary>Destinos das instruções que escrevem; <c>Determined</c> = false quando alguma não foi reconhecida.</summary>
    public static (List<SqlWriteTarget> Targets, bool Determined) Find(string sql, string? defaultCatalog, string? defaultSchema)
    {
        var targets = new List<SqlWriteTarget>();
        var determined = true;
        var catalog = defaultCatalog;
        var schema = defaultSchema;
        foreach (var statement in Statements(sql))
        {
            var kind = SqlStatementClassifier.Classify(statement).Kind;
            if (SessionChange.IsMatch(statement))
            {
                // USE/SET CATALOG no mesmo lote muda o padrão: daí em diante, nome curto não é confiável.
                catalog = schema = null;
                continue;
            }
            if (kind is not (SqlStatementKind.Write or SqlStatementKind.Schema)) continue;

            if (Create.Match(statement) is { Success: true } c)
            {
                if (c.Groups["mods"].Value.Contains("TEMP", StringComparison.OrdinalIgnoreCase)) continue; // só da sessão: não cria objeto
                targets.Add(Resolve(c.Groups["n"].Value, c.Groups["k"].Value.ToUpperInvariant(), catalog, schema));
                continue;
            }
            if (AlterDrop.Match(statement) is { Success: true } ad)
            {
                targets.Add(Resolve(ad.Groups["n"].Value, ad.Groups["k"].Value.ToUpperInvariant(), catalog, schema));
                continue;
            }
            if (Comment.Match(statement) is { Success: true } cm)
            {
                var k = cm.Groups["k"].Value.ToUpperInvariant();
                targets.Add(Resolve(cm.Groups["n"].Value, k == "COLUMN" ? "TABLE" : k, catalog, schema, column: k == "COLUMN"));
                continue;
            }
            var table = TableForms.Select(r => r.Match(statement)).FirstOrDefault(m => m.Success);
            if (table == null && statement.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
                table = InsideWith.Match(statement) is { Success: true } w ? w : null;
            if (table != null) targets.Add(Resolve(table.Groups["n"].Value, "TABLE", catalog, schema));
            else determined = false;
        }
        return (targets, determined);
    }

    /// <summary>Nome de 1 a 3 partes → catálogo, schema e objeto (SCHEMA/DATABASE: o próprio nome é o schema).</summary>
    private static SqlWriteTarget Resolve(string name, string kind, string? catalog, string? schema, bool column = false)
    {
        var parts = Regex.Matches(name, Part).Select(m => m.Value.Trim('`')).ToList();
        if (column && parts.Count > 1) parts.RemoveAt(parts.Count - 1); // COMMENT ON COLUMN t.col
        if (kind is "SCHEMA" or "DATABASE")
            return parts.Count switch
            {
                1 => new(catalog, parts[0], parts[0], kind),
                _ => new(parts[^2], parts[^1], parts[^1], kind),
            };
        if (kind == "CATALOG") return new(parts[^1], null, parts[^1], kind); // criar/alterar catálogo nunca é "dentro"
        return parts.Count switch
        {
            1 => new(catalog, schema, parts[0], kind),
            2 => new(catalog, parts[0], parts[1], kind),
            _ => new(parts[^3], parts[^2], parts[^1], kind),
        };
    }

    /// <summary>Instruções separadas por ';', sem comentários e com os textos entre aspas esvaziados.</summary>
    private static IEnumerable<string> Statements(string sql)
    {
        var current = new StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; current.Append(' '); continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 0;
                for (; i < sql.Length; i++)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i++; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i++; if (depth == 0) break; }
                }
                current.Append(' ');
                continue;
            }
            if (c is '\'' or '"')
            {
                for (i++; i < sql.Length; i++)
                {
                    if (sql[i] == '\\') { i++; continue; }
                    if (sql[i] == c) { if (i + 1 < sql.Length && sql[i + 1] == c) { i++; continue; } break; }
                }
                current.Append("''");
                continue;
            }
            if (c == '`')
            {
                var start = i;
                for (i++; i < sql.Length && sql[i] != '`'; i++) { }
                current.Append(sql, start, Math.Min(i, sql.Length - 1) - start + 1);
                continue;
            }
            if (c == ';') { if (current.ToString().Trim().Length > 0) yield return current.ToString(); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }
}
