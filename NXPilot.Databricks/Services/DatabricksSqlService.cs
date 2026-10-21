// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;

namespace NXPilot.Databricks;

/// <summary>Decide se uma instrução pode rodar. No NXPilot é o DecisionGate (+ o usuário).</summary>
public interface ISqlStatementApprover
{
    Task<SqlApproval> ApproveAsync(SqlStatementReview review, CancellationToken ct = default);
}

public sealed record SqlApproval(bool Approved, string Reason)
{
    public static SqlApproval Allow(string reason) => new(true, reason);
    public static SqlApproval Deny(string reason) => new(false, reason);
}

/// <summary>Padrão seguro do pacote: só leitura passa; o resto é negado.</summary>
public sealed class ReadOnlySqlApprover : ISqlStatementApprover
{
    public Task<SqlApproval> ApproveAsync(SqlStatementReview review, CancellationToken ct = default) =>
        Task.FromResult(review.IsReadOnly
            ? SqlApproval.Allow("leitura")
            : SqlApproval.Deny("somente leitura: escrita, DDL e permissões precisam de um aprovador"));
}

/// <summary>Camada de serviço do SQL Warehouse — é ela que o app (e os agentes) usam.</summary>
public interface IDatabricksSqlService
{
    /// <summary>Classifica, pede aprovação, executa, espera e junta todos os blocos do resultado.</summary>
    Task<SqlQueryResult> ExecuteAsync(string sql, IReadOnlyList<SqlParameter>? parameters = null,
        SqlExecutionOptions? options = null, CancellationToken ct = default);

    /// <summary>Recusa (sem perguntar a ninguém) tudo que não for leitura.</summary>
    Task<SqlQueryResult> QueryAsync(string sql, IReadOnlyList<SqlParameter>? parameters = null,
        SqlExecutionOptions? options = null, CancellationToken ct = default);

    /// <summary>SELECT 1 — confere host, token e warehouse (o warehouse parado pode levar minutos para ligar).</summary>
    Task TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListCatalogsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListSchemasAsync(string catalog, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListTablesAsync(string catalog, string schema, CancellationToken ct = default);

    /// <summary>Colunas da tabela (DESCRIBE TABLE) — contexto para o agente escrever o SQL.</summary>
    Task<IReadOnlyList<SqlColumn>> DescribeTableAsync(string catalog, string schema, string table, CancellationToken ct = default);
}

public sealed class DatabricksSqlService(DatabricksSqlClient client, ISqlStatementApprover? approver = null, Action<string>? log = null)
    : IDatabricksSqlService, IDisposable
{
    private readonly ISqlStatementApprover _approver = approver ?? new ReadOnlySqlApprover();

    public DatabricksSqlService(DatabricksConnection connection, ISqlStatementApprover? approver = null, Action<string>? log = null)
        : this(new DatabricksSqlClient(connection), approver, log) { }

    public async Task<SqlQueryResult> ExecuteAsync(string sql, IReadOnlyList<SqlParameter>? parameters = null,
        SqlExecutionOptions? options = null, CancellationToken ct = default)
    {
        var review = Review(sql);
        var approval = await _approver.ApproveAsync(review, ct);
        log?.Invoke($"{(approval.Approved ? "APROVADO" : "NEGADO")} ({approval.Reason}) — {review.Summary}");
        if (!approval.Approved) throw new SqlStatementDeniedException(review, approval.Reason);
        return await RunAsync(review, parameters, options ?? new SqlExecutionOptions(), ct);
    }

    public async Task<SqlQueryResult> QueryAsync(string sql, IReadOnlyList<SqlParameter>? parameters = null,
        SqlExecutionOptions? options = null, CancellationToken ct = default)
    {
        var review = Review(sql);
        if (!review.IsReadOnly) throw new SqlStatementDeniedException(review, "QueryAsync só executa leitura");
        return await RunAsync(review, parameters, options ?? new SqlExecutionOptions(), ct);
    }

    public async Task TestConnectionAsync(CancellationToken ct = default) =>
        await QueryAsync("SELECT 1 AS ok", options: new SqlExecutionOptions { MaxRows = 1 }, ct: ct);

    public async Task<IReadOnlyList<string>> ListCatalogsAsync(CancellationToken ct = default) =>
        FirstColumn(await QueryAsync("SHOW CATALOGS", ct: ct));

    public async Task<IReadOnlyList<string>> ListSchemasAsync(string catalog, CancellationToken ct = default) =>
        FirstColumn(await QueryAsync($"SHOW SCHEMAS IN {Quote(catalog)}", ct: ct));

    public async Task<IReadOnlyList<string>> ListTablesAsync(string catalog, string schema, CancellationToken ct = default)
    {
        var r = await QueryAsync($"SHOW TABLES IN {Quote(catalog)}.{Quote(schema)}", ct: ct);
        return r.Rows.Select((_, i) => r.Value(i, "tableName") ?? "").Where(t => t.Length > 0).ToList();
    }

    public async Task<IReadOnlyList<SqlColumn>> DescribeTableAsync(string catalog, string schema, string table, CancellationToken ct = default)
    {
        var r = await QueryAsync($"DESCRIBE TABLE {Quote(catalog)}.{Quote(schema)}.{Quote(table)}", ct: ct);
        var cols = new List<SqlColumn>();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var name = r.Value(i, "col_name") ?? "";
            if (name.Length == 0 || name.StartsWith('#')) break; // "# Partition Information" e afins
            var comment = r.Value(i, "comment");
            cols.Add(new SqlColumn(name, cols.Count, r.Value(i, "data_type") ?? "", r.Value(i, "data_type"), string.IsNullOrWhiteSpace(comment) ? null : comment));
        }
        return cols;
    }

    /// <summary>
    /// Comentário do objeto e das colunas (DESCRIBE TABLE EXTENDED: colunas no topo; "Comment" na
    /// parte "# Detailed Table Information"). Vale para tabela e view.
    /// </summary>
    public async Task<ObjectComments> CommentsAsync(string catalog, string schema, string table, CancellationToken ct = default)
    {
        var full = $"{Quote(catalog)}.{Quote(schema)}.{Quote(table)}";
        var r = await QueryAsync($"DESCRIBE TABLE EXTENDED {full}", options: new SqlExecutionOptions { MaxRows = 5000 }, ct: ct);
        var cols = new List<SqlColumn>();
        string? tableComment = null;
        var detailed = false;
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var name = r.Value(i, "col_name") ?? "";
            if (name.StartsWith("# Detailed", StringComparison.OrdinalIgnoreCase)) { detailed = true; continue; }
            if (detailed)
            {
                if (name.Equals("Comment", StringComparison.OrdinalIgnoreCase)) tableComment = r.Value(i, "data_type");
                continue;
            }
            if (name.Length == 0 || name.StartsWith('#')) continue; // "# Partition Information"…
            if (cols.Any(c => c.Name == name)) continue;            // colunas de partição repetidas
            var comment = r.Value(i, "comment");
            cols.Add(new SqlColumn(name, cols.Count, r.Value(i, "data_type") ?? "", r.Value(i, "data_type"), string.IsNullOrWhiteSpace(comment) ? null : comment));
        }
        return new ObjectComments($"{catalog}.{schema}.{table}", string.IsNullOrWhiteSpace(tableComment) ? null : tableComment, cols);
    }

    /// <summary>Identificador entre crases (crase interna dobrada) — nomes vindos da tela/agente não injetam SQL.</summary>
    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    private static SqlStatementReview Review(string sql)
    {
        var review = SqlStatementClassifier.Classify(sql);
        if (review.Kind == SqlStatementKind.Empty) throw new ArgumentException("SQL vazio.", nameof(sql));
        if (review.Statements.Count > 1)
            throw new SqlStatementDeniedException(review, "uma instrução por vez (a API do Databricks não executa scripts com ';')");
        return review;
    }

    private static IReadOnlyList<string> FirstColumn(SqlQueryResult r) =>
        r.Rows.Select(row => row.Count > 0 ? row[0] ?? "" : "").Where(v => v.Length > 0).ToList();

    /// <summary>
    /// Executa e espera: até 30 s dentro do próprio POST; depois consulta com intervalo crescente
    /// (0,5 s → 5 s). Cancelou (ct ou Timeout)? Cancela também no warehouse, para não deixar
    /// consulta rodando (e gastando) sem ninguém esperando.
    /// </summary>
    private async Task<SqlQueryResult> RunAsync(SqlStatementReview review, IReadOnlyList<SqlParameter>? parameters,
        SqlExecutionOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        string? statementId = null;
        try
        {
            var resp = await client.ExecuteAsync(review.Sql, parameters, options.MaxRows, 30, options.Catalog, options.Schema, timeout.Token);
            statementId = resp.StatementId;
            var delay = TimeSpan.FromMilliseconds(500);
            while (resp.Status.State is StatementState.Pending or StatementState.Running)
            {
                await Task.Delay(delay, timeout.Token);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
                resp = await client.GetAsync(statementId, timeout.Token);
            }

            if (resp.Status.State != StatementState.Succeeded)
            {
                var e = resp.Status.Error;
                throw new DatabricksException($"Instrução {resp.Status.State}: {e?.Message ?? "sem detalhe"}", e?.ErrorCode, resp.Status.SqlState);
            }
            statementId = null; // terminou: nada a cancelar

            var columns = resp.Manifest?.Schema?.Columns.Select(c => new SqlColumn(c.Name, c.Position, c.TypeName ?? "", c.TypeText)).ToList()
                          ?? new List<SqlColumn>();
            var rows = new List<IReadOnlyList<string?>>();
            var chunk = resp.Result;
            while (chunk != null)
            {
                if (chunk.DataArray != null) rows.AddRange(chunk.DataArray);
                if (chunk.NextChunkIndex is not { } next || rows.Count >= options.MaxRows) break;
                chunk = await client.GetChunkAsync(resp.StatementId, next, timeout.Token);
            }
            var truncated = (resp.Manifest?.Truncated ?? false) || rows.Count > options.MaxRows;
            if (rows.Count > options.MaxRows) rows.RemoveRange(options.MaxRows, rows.Count - options.MaxRows);
            log?.Invoke($"Databricks: {rows.Count} linha(s){(truncated ? " (cortado)" : "")} em {sw.Elapsed.TotalSeconds:F1}s — {resp.StatementId}");
            return new SqlQueryResult(resp.StatementId, columns, rows, resp.Manifest?.TotalRowCount ?? rows.Count, truncated, review.Kind, sw.Elapsed);
        }
        catch (OperationCanceledException) when (statementId != null)
        {
            try { await client.CancelAsync(statementId, CancellationToken.None); } catch { /* melhor esforço */ }
            log?.Invoke($"Databricks: instrução {statementId} cancelada.");
            if (!ct.IsCancellationRequested) throw new TimeoutException($"A instrução passou de {options.Timeout.TotalMinutes:F0} min e foi cancelada no warehouse.");
            throw;
        }
    }

    public void Dispose() => client.Dispose();
}
