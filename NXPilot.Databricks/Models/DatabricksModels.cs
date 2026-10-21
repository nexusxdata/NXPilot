// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXPilot.Databricks;

/// <summary>
/// Conexão com um SQL Warehouse: host do workspace, token (PAT ou OAuth) e o warehouse.
/// O token só vive em memória — quem guarda (DPAPI no NXPilot) é o chamador.
/// </summary>
public sealed record DatabricksConnection
{
    public DatabricksConnection(string host, string token, string warehouseId, string? catalog = null, string? schema = null)
    {
        Host = NormalizeHost(host);
        Token = token;
        WarehouseId = NormalizeWarehouseId(warehouseId);
        Catalog = string.IsNullOrWhiteSpace(catalog) ? null : catalog.Trim();
        Schema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();
    }

    /// <summary>Sempre "https://host", sem barra no fim (aceita com ou sem https://).</summary>
    public string Host { get; }
    public string Token { get; }
    public string WarehouseId { get; }
    public string? Catalog { get; }
    public string? Schema { get; }

    public bool IsComplete => Host.Length > "https://".Length && Token.Length > 0 && WarehouseId.Length > 0;

    /// <summary>Variáveis padrão do Databricks: DATABRICKS_HOST, DATABRICKS_TOKEN e DATABRICKS_WAREHOUSE_ID.</summary>
    public static DatabricksConnection? FromEnvironment()
    {
        var c = new DatabricksConnection(Environment.GetEnvironmentVariable("DATABRICKS_HOST") ?? "",
            Environment.GetEnvironmentVariable("DATABRICKS_TOKEN") ?? "",
            Environment.GetEnvironmentVariable("DATABRICKS_WAREHOUSE_ID") ?? "");
        return c.IsComplete ? c : null;
    }

    /// <summary>O token nunca aparece em log, exceção ou ToString.</summary>
    public override string ToString() => $"{Host} (warehouse {WarehouseId})";

    internal static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('/');
        if (host.Length == 0) return "";
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host["http://".Length..];
        return host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : "https://" + host;
    }

    /// <summary>Aceita o ID puro ou o "HTTP path" da tela do warehouse (/sql/1.0/warehouses/&lt;id&gt;).</summary>
    internal static string NormalizeWarehouseId(string value)
    {
        value = value.Trim().TrimEnd('/');
        var i = value.LastIndexOf('/');
        return i >= 0 ? value[(i + 1)..] : value;
    }
}

/// <summary>Parâmetro nomeado (:nome no SQL). Value null = NULL; Type no formato do Databricks (INT, DECIMAL(10,2)…).</summary>
public sealed record SqlParameter(string Name, string? Value, string? Type = null);

public sealed record SqlColumn(string Name, int Position, string TypeName, string? TypeText, string? Comment = null);

/// <summary>Comentários de um objeto (tabela ou view): o do objeto e o de cada coluna.</summary>
public sealed record ObjectComments(string FullName, string? Comment, IReadOnlyList<SqlColumn> Columns)
{
    /// <summary>Texto como literal SQL do Databricks ('...' com \' e \\ escapados).</summary>
    public static string Literal(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>
    /// SQL pronto para levar os comentários a um objeto novo: para VIEW, a lista de colunas com
    /// COMMENT e o COMMENT da view (vão no CREATE VIEW); para TABELA, COMMENT ON e ALTER COLUMN.
    /// </summary>
    public string Snippets(string target)
    {
        var sb = new System.Text.StringBuilder();
        var commented = Columns.Where(c => !string.IsNullOrWhiteSpace(c.Comment)).ToList();
        sb.AppendLine("-- VIEW: coloque a lista de colunas e o COMMENT no CREATE VIEW (colunas novas/renomeadas: escreva o comentário delas)");
        sb.AppendLine($"CREATE OR REPLACE VIEW {target} (");
        sb.AppendLine(string.Join(",\n", Columns.Select(c => $"  `{c.Name.Replace("`", "``")}`" + (string.IsNullOrWhiteSpace(c.Comment) ? "" : $" COMMENT {Literal(c.Comment!)}"))));
        sb.AppendLine(")" + (string.IsNullOrWhiteSpace(Comment) ? "" : $"\nCOMMENT {Literal(Comment!)}"));
        sb.AppendLine("AS SELECT …;");
        sb.AppendLine();
        sb.AppendLine("-- TABELA: depois de criar");
        if (!string.IsNullOrWhiteSpace(Comment)) sb.AppendLine($"COMMENT ON TABLE {target} IS {Literal(Comment!)};");
        foreach (var c in commented) sb.AppendLine($"ALTER TABLE {target} ALTER COLUMN `{c.Name.Replace("`", "``")}` COMMENT {Literal(c.Comment!)};");
        return sb.ToString();
    }
}

/// <summary>Resultado de uma instrução: colunas, linhas (valores como texto, null = NULL) e se foi cortado.</summary>
public sealed record SqlQueryResult(
    string StatementId,
    IReadOnlyList<SqlColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long TotalRowCount,
    bool Truncated,
    SqlStatementKind Kind,
    TimeSpan Elapsed)
{
    /// <summary>Valor por nome de coluna (sem diferenciar maiúsculas).</summary>
    public string? Value(int row, string column)
    {
        var col = Columns.FirstOrDefault(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"Coluna '{column}' não existe no resultado.", nameof(column));
        return Rows[row][col.Position];
    }
}

/// <summary>Opções de execução. MaxRows protege a memória do app (o warehouse corta e marca Truncated).</summary>
public sealed record SqlExecutionOptions
{
    public int MaxRows { get; init; } = 10_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public string? Catalog { get; init; }
    public string? Schema { get; init; }
}

/// <summary>Erro devolvido pelo Databricks (HTTP ou instrução FAILED). Nunca carrega o token.</summary>
public class DatabricksException(string message, string? errorCode = null, string? sqlState = null, int? httpStatus = null)
    : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
    public string? SqlState { get; } = sqlState;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>A instrução não foi aprovada (gate do NXPilot, usuário ou política somente leitura).</summary>
public sealed class SqlStatementDeniedException(SqlStatementReview review, string reason)
    : Exception($"Instrução SQL não aprovada ({review.Kind}): {reason}")
{
    public SqlStatementReview Review { get; } = review;
}
