// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.Ai;
using NXPilot.Databricks;
using NXPilot.GitRepos;
using NXPilot.Risk;

namespace NXPilot.Integrations;

/// <summary>
/// Tokens do GitHub e do Azure DevOps (DPAPI) em %LOCALAPPDATA%\NXPilot\git-repos.json.
/// Sem token do DevOps salvo, usa o PAT que o NXProject já tem (somente leitura do arquivo dele).
/// </summary>
public sealed class GitRepoSettings : IGitCredentialProvider
{
    public string AzureDevOpsOrganizationUrl { get; set; } = "";
    [JsonIgnore] public string GitHubToken { get; set; } = "";
    [JsonIgnore] public string AzureDevOpsToken { get; set; } = "";
    public string GitHubTokenProtected { get; set; } = "";
    public string AzureDevOpsTokenProtected { get; set; } = "";

    /// <summary>O PAT do DevOps veio do NXProject (não é gravado no arquivo do NXPilot).</summary>
    [JsonIgnore] public bool AzureDevOpsFromNXProject { get; set; }

    public GitCredential? For(GitProvider provider) => provider switch
    {
        GitProvider.GitHub when GitHubToken.Length > 0 => new(provider, GitHubToken),
        GitProvider.AzureDevOps when AzureDevOpsToken.Length > 0 => new(provider, AzureDevOpsToken),
        _ => null,
    };

    private static string FilePath => NXPilot.Integrations.ExtensionSettings.File("git-repos.json");

    [SupportedOSPlatform("windows")]
    public static GitRepoSettings Load()
    {
        var s = new GitRepoSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<GitRepoSettings>(File.ReadAllText(FilePath)) is { } saved) s = saved;
        }
        catch { /* arquivo inválido: começa vazio */ }
        s.GitHubToken = Secret.Unprotect(s.GitHubTokenProtected);
        s.AzureDevOpsToken = Secret.Unprotect(s.AzureDevOpsTokenProtected);
        if (s.AzureDevOpsToken.Length == 0 && DevOpsConnection.FromNXProject() is { } nx)
        {
            s.AzureDevOpsToken = nx.Pat;
            s.AzureDevOpsFromNXProject = true;
            if (s.AzureDevOpsOrganizationUrl.Length == 0) s.AzureDevOpsOrganizationUrl = nx.OrganizationUrl;
        }
        return s;
    }

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        GitHubTokenProtected = Secret.Protect(GitHubToken);
        AzureDevOpsTokenProtected = AzureDevOpsFromNXProject ? "" : Secret.Protect(AzureDevOpsToken);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Liga o pacote NXPilot.GitRepos ao DecisionGate: clone, pull e commit em branch de trabalho são
/// BAIXO; commit na branch principal, push em branch de trabalho, PR e deploy são MÉDIO; push (e
/// deploy) na branch principal é ALTO. Sem <paramref name="askUser"/>, o que não passa sozinho é negado.
/// </summary>
public sealed class GateGitApprover(Func<GitOperationReview, RiskResult, CancellationToken, Task<bool>>? askUser = null)
    : IGitOperationApprover
{
    public static RiskResult Risk(GitOperationReview r)
    {
        var level = r.Kind switch
        {
            GitOperationKind.Clone or GitOperationKind.Pull => RiskLevel.Baixo,
            GitOperationKind.Commit => r.TargetsDefaultBranch ? RiskLevel.Medio : RiskLevel.Baixo,
            GitOperationKind.Push or GitOperationKind.Deploy => r.TargetsDefaultBranch ? RiskLevel.Alto : RiskLevel.Medio,
            _ => RiskLevel.Medio, // PullRequest: sai da máquina, mas ainda passa por revisão no servidor
        };
        return new RiskResult(level, 0.9, "Git", r.Kind + (r.TargetsDefaultBranch ? " na branch principal" : ""), TimeSpan.Zero);
    }

    public async Task<GitApproval> ApproveAsync(GitOperationReview review, CancellationToken ct = default)
    {
        var risk = Risk(review);
        if (DecisionGate.Decide(risk) == GateOutcome.AutoApprove) return GitApproval.Allow($"gate: {risk.Level}");
        if (askUser == null) return GitApproval.Deny($"gate: {risk.Level} — ninguém para aprovar");
        return await askUser(review, risk, ct)
            ? GitApproval.Allow($"usuário aprovou ({risk.Level})")
            : GitApproval.Deny($"usuário negou ({risk.Level})");
    }
}

/// <summary>
/// Leva o código ao Databricks: depois do push, manda o Git folder do workspace fazer checkout
/// da branch (Repos API). É um Deploy — passa pelo mesmo aprovador do git.
/// </summary>
public static class GitDatabricksSync
{
    public static async Task<DatabricksRepo> SyncAsync(DatabricksReposClient repos, string workspacePath, string branch,
        IGitOperationApprover approver, string? defaultBranch = "main", CancellationToken ct = default)
    {
        var repo = await repos.FindByPathAsync(workspacePath, ct)
                   ?? throw new GitRepoException($"Git folder não encontrado no Databricks: {workspacePath}");
        var review = new GitOperationReview(GitOperationKind.Deploy, repo.Url ?? workspacePath, branch,
            string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase),
            $"Databricks {workspacePath}: {repo.Branch ?? "?"} → {branch}");
        var approval = await approver.ApproveAsync(review, ct);
        if (!approval.Approved) throw new GitOperationDeniedException(review, approval.Reason);
        return await repos.CheckoutBranchAsync(repo.Id, branch, ct);
    }
}
