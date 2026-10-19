// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.Ai;

namespace NXPilot.Integrations;

/// <summary>De onde veio a conexão usada pelos Work items.</summary>
public enum WorkItemConnectionOrigin { Own, NXProject }

/// <summary>Projeto do GitHub Projects (v2): dono (usuário ou organização), número e token (decifrado só em memória).</summary>
public sealed record GitHubProjectConnection(string Owner, bool OwnerIsOrganization, int Number, string Token)
{
    public string WebUrl => $"https://github.com/{(OwnerIsOrganization ? "orgs" : "users")}/{Owner}/projects/{Number}";

    /// <summary>A conexão do GitHub Projects que o NXProject já tem (somente leitura do arquivo dele).</summary>
    [SupportedOSPlatform("windows")]
    public static GitHubProjectConnection? FromNXProject()
    {
        try
        {
            if (NXProjectConfig.Read() is not { } root) return null;
            var owner = NXProjectConfig.Str(root, "GitOwner");
            var enc = NXProjectConfig.Str(root, "GitEncryptedToken");
            var number = root.TryGetProperty("GitProjectNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            var isOrg = root.TryGetProperty("GitOwnerIsOrganization", out var o) && o.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(owner) || number <= 0 || string.IsNullOrWhiteSpace(enc)) return null;
            var token = NXProjectConfig.Decrypt(enc);
            return token.Length == 0 ? null : new GitHubProjectConnection(owner, isOrg, number, token);
        }
        catch { return null; }
    }
}

/// <summary>Leitura do config_nxproject.json do NXProject (só leitura; tokens em DPAPI do mesmo usuário).</summary>
public static class NXProjectConfig
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject.Community", "config_nxproject.json");

    public static JsonElement? Read()
    {
        if (!File.Exists(FilePath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        return doc.RootElement.Clone();
    }

    public static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Nomes do tipo de work item do nível Project ("Project, Projeto, Work Item Project" por padrão).</summary>
    public static IReadOnlyList<string> ProjectLevelTypes()
    {
        string? names = null;
        try
        {
            if (Read() is { } root && root.TryGetProperty("LevelNames", out var levels) && levels.ValueKind == JsonValueKind.Object)
                names = Str(levels, "Project");
        }
        catch { /* sem NXProject: usa o padrão */ }
        return (string.IsNullOrWhiteSpace(names) ? "Project, Projeto, Work Item Project" : names)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Nome do campo de nível do board do GitHub (o "card_type" do NXProject); padrão cardType.</summary>
    public static string GitLevelField()
    {
        try
        {
            if (Read() is { } root && root.TryGetProperty("GitFieldNames", out var names) && names.ValueKind == JsonValueKind.Object
                && Str(names, "cardType") is { Length: > 0 } field) return field;
        }
        catch { /* sem NXProject: usa o padrão */ }
        return "cardType";
    }

    /// <summary>Issue raiz (Project) da última importação do GitHub no NXProject, com o board dela; nulo sem.</summary>
    public static (string Owner, int Board, int RootIssue)? GitRootIssue()
    {
        try
        {
            if (Read() is not { } root) return null;
            var issue = root.TryGetProperty("GitLastRootIssue", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            var board = root.TryGetProperty("GitProjectNumber", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            return issue > 0 && Str(root, "GitOwner") is { Length: > 0 } owner ? (owner, board, issue) : null;
        }
        catch { return null; }
    }

    /// <summary>Work item raiz (Project) configurado no NXProject e o Team Project dele; nulo sem NXProject.</summary>
    public static (string TeamProject, int RootId)? RootWorkItem()
    {
        try
        {
            if (Read() is not { } root) return null;
            var id = root.TryGetProperty("RootWorkItemId", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return id > 0 && Str(root, "TeamProject") is { Length: > 0 } project ? (project, id) : null;
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    public static string Decrypt(string base64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser));
}

/// <summary>
/// Extensão Work items: Azure DevOps e GitHub Projects, em %LOCALAPPDATA%\NXPilot\work-items.json
/// (tokens em DPAPI). Cada origem usa a configuração própria quando ela está completa; senão, a
/// do NXProject — que o NXPilot só lê, nunca grava.
/// </summary>
public sealed class WorkItemSettings
{
    public string DevOpsOrganizationUrl { get; set; } = "";
    public string DevOpsProject { get; set; } = "";
    public string DevOpsPatProtected { get; set; } = "";
    [JsonIgnore] public string DevOpsPat { get; set; } = "";

    public string GitHubOwner { get; set; } = "";
    public bool GitHubOwnerIsOrganization { get; set; }
    public int GitHubProjectNumber { get; set; }
    public string GitHubTokenProtected { get; set; } = "";
    [JsonIgnore] public string GitHubToken { get; set; } = "";

    /// <summary>Última origem escolhida na tela de Work items ("devops" ou "github").</summary>
    public string LastSource { get; set; } = "";

    /// <summary>Último projeto escolhido na tela de Work items (vazio/0 = o da conexão).</summary>
    public string LastDevOpsProject { get; set; } = "";
    public int LastGitHubProjectNumber { get; set; }

    /// <summary>Último work item Project escolhido (0 = todos; nulo = ainda não escolheu: começa na raiz do NXProject).</summary>
    public int? LastDevOpsRootId { get; set; }

    /// <summary>Última issue Project escolhida no GitHub (0 = todas; nulo = começa na raiz do NXProject).</summary>
    public int? LastGitHubRootIssue { get; set; }

    [JsonIgnore] public bool HasOwnDevOps => DevOpsOrganizationUrl.Length > 0 && DevOpsProject.Length > 0 && DevOpsPat.Length > 0;
    [JsonIgnore] public bool HasOwnGitHub => GitHubOwner.Length > 0 && GitHubProjectNumber > 0 && GitHubToken.Length > 0;

    /// <summary>Azure DevOps a usar: a própria, completa; senão a do NXProject; senão nenhuma.</summary>
    [SupportedOSPlatform("windows")]
    public (DevOpsConnection Connection, WorkItemConnectionOrigin Origin)? DevOps() =>
        HasOwnDevOps ? (new DevOpsConnection(DevOpsOrganizationUrl.TrimEnd('/'), DevOpsProject, DevOpsPat), WorkItemConnectionOrigin.Own)
        : DevOpsConnection.FromNXProject() is { } nx ? (nx, WorkItemConnectionOrigin.NXProject) : null;

    /// <summary>GitHub Projects a usar: o próprio, completo; senão o do NXProject; senão nenhum.</summary>
    [SupportedOSPlatform("windows")]
    public (GitHubProjectConnection Connection, WorkItemConnectionOrigin Origin)? GitHub() =>
        HasOwnGitHub ? (new GitHubProjectConnection(GitHubOwner, GitHubOwnerIsOrganization, GitHubProjectNumber, GitHubToken), WorkItemConnectionOrigin.Own)
        : GitHubProjectConnection.FromNXProject() is { } nx ? (nx, WorkItemConnectionOrigin.NXProject) : null;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "work-items.json");

    [SupportedOSPlatform("windows")]
    public static WorkItemSettings Load()
    {
        var s = new WorkItemSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<WorkItemSettings>(File.ReadAllText(FilePath)) is { } saved) s = saved;
        }
        catch { /* arquivo inválido: começa vazio */ }
        s.DevOpsPat = Secret.Unprotect(s.DevOpsPatProtected);
        s.GitHubToken = Secret.Unprotect(s.GitHubTokenProtected);
        return s;
    }

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        DevOpsPatProtected = Secret.Protect(DevOpsPat);
        GitHubTokenProtected = Secret.Protect(GitHubToken);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
