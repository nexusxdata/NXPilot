// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.Ai;
using NXPilot.Databricks;
using NXPilot.Risk;

namespace NXPilot.Integrations;

/// <summary>
/// Conexão Databricks do NXPilot: host, warehouse, catálogo/schema padrão e o token cifrado
/// com DPAPI (perfil do usuário) em %LOCALAPPDATA%\NXPilot\databricks.json.
/// </summary>
public sealed class DatabricksSettings
{
    public string Host { get; set; } = "";
    public string WarehouseId { get; set; } = "";
    public string Catalog { get; set; } = "";
    public string Schema { get; set; } = "";

    /// <summary>
    /// Schemas onde o agente pode criar e alterar objetos: nomes completos ou com curinga
    /// ("sandbox_*", "dev.sandbox_*"), separados por vírgula. Vazio = escrita segue o gate de sempre
    /// (ALTO, você decide). Com padrões, escrever fora deles é bloqueado — protege a produção.
    /// </summary>
    public string WriteSchemas { get; set; } = "";

    public WriteSchemaPolicy WritePolicy() => new(WriteSchemas, Catalog, Schema);

    [JsonIgnore] public string Token { get; set; } = "";
    public string TokenProtected { get; set; } = "";

    public DatabricksConnection ToConnection() => new(Host, Token, WarehouseId, Catalog, Schema);

    internal static string FilePath => NXPilot.Integrations.ExtensionSettings.File("databricks.json");

    /// <summary>Campos salvos (token decifrado só em memória); sem arquivo, vazio.</summary>
    [SupportedOSPlatform("windows")]
    public static DatabricksSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<DatabricksSettings>(File.ReadAllText(FilePath)) is { } s)
            {
                s.Token = Secret.Unprotect(s.TokenProtected);
                return s;
            }
        }
        catch { /* arquivo inválido */ }
        return new DatabricksSettings();
    }

    /// <summary>Configuração salva; sem arquivo, cai nas variáveis DATABRICKS_* (útil no Bench).</summary>
    [SupportedOSPlatform("windows")]
    public static DatabricksConnection? LoadConnection() =>
        Load().ToConnection() is { IsComplete: true } c ? c : DatabricksConnection.FromEnvironment();

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        TokenProtected = Secret.Protect(Token);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Liga o pacote NXPilot.Databricks ao DecisionGate: leitura é BAIXO (passa sozinha); sessão e
/// SQL desconhecido são MÉDIO; escrita, DDL e permissões são ALTO. O que não for aprovado sozinho
/// vai para <paramref name="askUser"/> — sem ele, nega (nunca libera por omissão).
/// </summary>
public sealed class GateSqlApprover(Func<SqlStatementReview, RiskResult, CancellationToken, Task<bool>>? askUser = null,
    WriteSchemaPolicy? writePolicy = null)
    : ISqlStatementApprover
{
    /// <summary>
    /// Com schemas de gravação: escrita/DDL só DENTRO deles é MÉDIO (você ou a regra da tarefa
    /// liberam); FORA é bloqueada sem perguntar; destino que não deu para identificar é ALTO.
    /// Permissões (GRANT/REVOKE) continuam ALTO. Sem schemas de gravação, nada muda.
    /// </summary>
    public static (RiskResult Risk, string? Blocked) Evaluate(SqlStatementReview review, WriteSchemaPolicy? policy)
    {
        if (policy is not { IsActive: true } || review.Kind is not (SqlStatementKind.Write or SqlStatementKind.Schema))
            return (Risk(review), null);
        var (scope, _, detail) = policy.Evaluate(review.Sql);
        return scope switch
        {
            WriteScope.Allowed => (new(RiskLevel.Medio, 0.95, "SQL", $"{review.Kind}{(review.IsDestructive ? ", destrutivo" : "")} · {detail}", TimeSpan.Zero), null),
            WriteScope.Blocked => (new(RiskLevel.Alto, 1, "SQL", detail, TimeSpan.Zero), detail),
            _ => (new(RiskLevel.Alto, 0.9, "SQL", $"{review.Kind} · {detail}", TimeSpan.Zero), null),
        };
    }

    public static RiskResult Risk(SqlStatementReview review) => review.Kind switch
    {
        SqlStatementKind.Read or SqlStatementKind.Empty => new(RiskLevel.Baixo, 0.95, "SQL", "leitura", TimeSpan.Zero),
        SqlStatementKind.Session => new(RiskLevel.Medio, 0.9, "SQL", "altera a sessão (USE/SET)", TimeSpan.Zero),
        SqlStatementKind.Unknown => new(RiskLevel.Medio, 0.5, "SQL", "instrução não reconhecida", TimeSpan.Zero),
        _ => new(RiskLevel.Alto, 0.95, "SQL", review.IsDestructive ? $"{review.Kind}, destrutivo" : review.Kind.ToString(), TimeSpan.Zero),
    };

    public async Task<SqlApproval> ApproveAsync(SqlStatementReview review, CancellationToken ct = default)
    {
        var (risk, blocked) = Evaluate(review, writePolicy);
        if (blocked != null)
            return SqlApproval.Deny($"bloqueado pelos schemas de gravação: {blocked}. Crie/altere objetos só em {writePolicy!.PatternsText}.");
        if (DecisionGate.Decide(risk) == GateOutcome.AutoApprove) return SqlApproval.Allow($"gate: {risk.Level}");
        if (askUser == null) return SqlApproval.Deny($"gate: {risk.Level} — ninguém para aprovar");
        return await askUser(review, risk, ct)
            ? SqlApproval.Allow($"usuário aprovou ({risk.Level})")
            : SqlApproval.Deny($"usuário negou ({risk.Level})");
    }
}
