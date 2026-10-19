// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using LibGit2Sharp;

namespace NXPilot.Agents;

/// <summary>Um checkpoint da tarefa (fase ou correção) no branch dela.</summary>
public sealed record TaskCommit(string Sha, string Message, DateTime When)
{
    public string ShortSha => Sha[..Math.Min(8, Sha.Length)];
}

/// <summary>
/// Um git worktree por tarefa: o agente trabalha numa cópia isolada, num branch próprio
/// (nxpilot/&lt;tarefa&gt;), e nunca mexe na pasta em que você está trabalhando. Juntar o
/// resultado (merge/PR) continua sendo decisão sua. Git embutido (LibGit2Sharp): não precisa
/// do Git instalado. A origem é o seu repositório ou o repositório-sombra (<see cref="ShadowRepo"/>).
/// </summary>
public static class GitWorktree
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "worktrees");

    internal static Signature Author => new("NXPilot", "nxpilot@local", DateTimeOffset.Now);

    /// <summary>Valida o repositório e cria o worktree; devolve (pasta, branch).</summary>
    /// <param name="displayName">Nome da pasta do worktree (o repositório-sombra não tem pasta de trabalho própria).</param>
    public static Task<(string Path, string Branch)> CreateAsync(string repoPath, string taskId, CancellationToken ct = default, string? displayName = null) => Task.Run(() =>
    {
        using var repo = Open(repoPath);
        if (repo.Head.Tip == null)
            throw new InvalidOperationException(NxText.T("O repositório não tem nenhum commit ainda.", "The repository has no commits yet.", "El repositorio aún no tiene ningún commit."));

        var name = displayName ?? new DirectoryInfo(repo.Info.WorkingDirectory ?? repoPath).Name;
        var path = Path.Combine(Root, $"{name}-{taskId}");
        var branch = $"nxpilot/{taskId}";
        Directory.CreateDirectory(Root);
        // A libgit2 cria o branch com o nome do worktree (sem "/"); renomeado em seguida.
        repo.Worktrees.Add($"nxpilot-{taskId}", path, false);
        repo.Branches.Rename($"nxpilot-{taskId}", branch);
        if (!Directory.Exists(path))
            throw new InvalidOperationException(NxText.T("Não foi possível criar o worktree.", "Could not create the worktree.", "No fue posible crear el worktree."));
        return (path, branch);
    }, ct);

    /// <summary>Commit de onde o worktree nasceu — base do diff da tarefa inteira.</summary>
    public static Task<string> HeadAsync(string worktree, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(worktree);
        return repo.Head.Tip?.Sha ?? "";
    }, ct);

    /// <summary>
    /// Diff do trabalho do agente desde <paramref name="baseCommit"/> (commits das fases + o que
    /// ainda não foi commitado), incluindo arquivos novos (vão para o índice antes do diff).
    /// </summary>
    public static Task<string> DiffAsync(string worktree, string? baseCommit = null, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(worktree);
        Commands.Stage(repo, "*");
        var tree = baseCommit is { Length: > 0 } ? repo.Lookup<Commit>(baseCommit)?.Tree : repo.Head.Tip?.Tree;
        return repo.Diff.Compare<Patch>(tree, DiffTargets.Index).Content;
    }, ct);

    /// <summary>Commits da tarefa desde a base (um por fase/correção), do mais antigo para o mais novo.</summary>
    public static Task<IReadOnlyList<TaskCommit>> CommitsAsync(string worktree, string? baseCommit, CancellationToken ct = default) => Task.Run<IReadOnlyList<TaskCommit>>(() =>
    {
        using var repo = new Repository(worktree);
        if (repo.Head.Tip == null) return [];
        var filter = new CommitFilter { IncludeReachableFrom = repo.Head.Tip, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse };
        if (baseCommit is { Length: > 0 }) filter.ExcludeReachableFrom = baseCommit;
        return repo.Commits.QueryBy(filter)
            .Select(c => new TaskCommit(c.Sha, c.MessageShort, c.Author.When.LocalDateTime))
            .ToList();
    }, ct);

    /// <summary>O que um commit mudou em relação ao commit anterior.</summary>
    public static Task<string> CommitDiffAsync(string worktree, string sha, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(worktree);
        var commit = repo.Lookup<Commit>(sha) ?? throw new InvalidOperationException("commit " + sha);
        return repo.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree).Content;
    }, ct);

    /// <summary>Arquivos que a tarefa criou ou alterou desde <paramref name="baseCommit"/> (apagados ficam de fora).</summary>
    public static Task<IReadOnlyList<string>> ChangedFilesAsync(string worktree, string? baseCommit, CancellationToken ct = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var repo = new Repository(worktree);
        Commands.Stage(repo, "*");
        var tree = baseCommit is { Length: > 0 } ? repo.Lookup<Commit>(baseCommit)?.Tree : null;
        return repo.Diff.Compare<TreeChanges>(tree, DiffTargets.Index)
            .Where(c => c.Status is not (ChangeKind.Deleted or ChangeKind.Unmodified or ChangeKind.Ignored or ChangeKind.Untracked))
            .Select(c => c.Path).ToList();
    }, ct);

    /// <summary>
    /// Checkpoint ao fim de cada fase, feito pelo NXPilot (não pelo agente): cada fase vira
    /// um commit no branch da tarefa. Devolve false quando a fase não mudou nada.
    /// </summary>
    public static Task<bool> CommitAllAsync(string worktree, string message, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(worktree);
        Commands.Stage(repo, "*");
        if (repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index).Count == 0) return false;
        repo.Commit(message, Author, Author);
        return true;
    }, ct);

    /// <summary>Apaga o worktree e o branch da tarefa (Histórico → remover). Irreversível.</summary>
    public static Task RemoveAsync(string repoPath, string worktree, string? branch, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = Open(repoPath);
        var full = Path.GetFullPath(worktree).TrimEnd('\\', '/');
        // Registro do worktree em <gitdir>/worktrees/<nome>: o arquivo "gitdir" aponta para <worktree>\.git.
        var admin = Path.Combine(repo.Info.Path, "worktrees");
        if (Directory.Exists(admin))
            foreach (var dir in Directory.GetDirectories(admin))
            {
                var gitdirFile = Path.Combine(dir, "gitdir");
                if (!File.Exists(gitdirFile)) continue;
                var target = Path.GetDirectoryName(Path.GetFullPath(File.ReadAllText(gitdirFile).Trim()))?.TrimEnd('\\', '/');
                if (string.Equals(target, full, StringComparison.OrdinalIgnoreCase)) ForceDelete(dir);
            }
        if (Directory.Exists(full)) ForceDelete(full);
        if (!string.IsNullOrEmpty(branch) && repo.Branches[branch] != null) repo.Branches.Remove(branch);
    }, ct);

    internal static Repository Open(string repoPath)
    {
        try { return new Repository(repoPath); }
        catch (RepositoryNotFoundException)
        {
            throw new InvalidOperationException(NxText.T($"{repoPath} não é um repositório git.", $"{repoPath} is not a git repository.", $"{repoPath} no es un repositorio git."));
        }
    }

    /// <summary>Apaga a pasta mesmo com arquivos somente leitura (objetos do git).</summary>
    internal static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}
