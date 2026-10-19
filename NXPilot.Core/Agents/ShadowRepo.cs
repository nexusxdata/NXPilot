// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace NXPilot.Agents;

/// <summary>Como a tarefa é versionada: repositório-sombra de um assunto ou o repositório git do projeto.</summary>
public enum VersioningMode { Local, Git }

/// <summary>
/// Assunto: uma subpasta comum (<see cref="Path"/>, ex.: Documentos\NXPilot\Relatórios) onde
/// ficam os arquivos. O histórico fica ao lado, oculto, em &lt;pai&gt;\.nxpilot\&lt;nome&gt;.
/// </summary>
public sealed class ShadowSubject
{
    public string Path { get; set; } = "";

    [JsonIgnore] public string Name => new DirectoryInfo(Path).Name;

    /// <summary>Com o agrupador (a pasta onde o assunto está, normalmente a pasta dos assuntos): "NXPilot › testePilot01".</summary>
    [JsonIgnore] public string GroupedName =>
        System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path.TrimEnd('\\', '/'))) is { Length: > 0 } group ? $"{group} › {Name}" : Name;

    /// <summary>Repositório-sombra (histórico) do assunto.</summary>
    [JsonIgnore] public string Dir => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, ShadowRepo.HistoryFolder, Name);

    /// <summary>Remoto no GitHub, se o assunto já foi movido para lá (lido do próprio repositório).</summary>
    [JsonIgnore] public string? RemoteUrl { get; internal set; }

    [JsonIgnore] public string? WebUrl => RemoteUrl is { } url && url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : RemoteUrl;
}

/// <summary>Resultado de levar a tarefa para a pasta do assunto.</summary>
/// <param name="Conflicts">Arquivos que você também mudou na pasta depois do snapshot; com conflito nada é gravado.</param>
public sealed record ApplyResult(int Written, int Deleted, List<string> Conflicts);

/// <summary>
/// Versionamento local: cada assunto é uma subpasta da pasta de assuntos (<see cref="Root"/>),
/// sem git e sem .git dentro dela. Cada tarefa tira um snapshot do assunto para o histórico e
/// trabalha num worktree a partir dele; a pasta só muda quando você aplica o resultado.
/// </summary>
public static class ShadowRepo
{
    /// <summary>Pasta oculta, ao lado dos assuntos, com o histórico de cada um.</summary>
    public const string HistoryFolder = ".nxpilot";

    public static string DefaultRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NXPilot");

    private static string? _root;

    /// <summary>
    /// Pasta dos assuntos: o padrão (Documentos\NXPilot) ou uma escolhida (ex.: OneDrive/Google
    /// Drive, para backup). Assunto novo nasce aqui; todas as subpastas dela são assuntos.
    /// </summary>
    public static string Root
    {
        get => _root is { Length: > 0 } ? _root : DefaultRoot;
        set => _root = value;
    }

    /// <summary>
    /// Assuntos fora da pasta atual (movidos ou de uma pasta anterior) — lista no perfil local.
    /// Trocável só para os testes não tocarem na lista real.
    /// </summary>
    public static string SubjectsFile { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "subjects.json");

    /// <summary>Pastas pesadas ou geradas que não entram no snapshot (além do .gitignore da própria pasta).</summary>
    private const string DefaultExcludes = "bin/\nobj/\nnode_modules/\n.vs/\npackages/\n*.tmp\n~$*\nThumbs.db\ndesktop.ini\n";

    /// <summary>Subpastas da pasta de assuntos + assuntos lembrados em outros lugares (os que ainda existem).</summary>
    public static List<ShadowSubject> LoadSubjects()
    {
        var paths = new List<string>();
        try
        {
            if (File.Exists(SubjectsFile) && JsonSerializer.Deserialize<List<ShadowSubject>>(File.ReadAllText(SubjectsFile)) is { } saved)
                paths.AddRange(saved.Select(s => s.Path));
        }
        catch { /* arquivo inválido: só a pasta atual */ }
        if (Directory.Exists(Root))
            paths.AddRange(Directory.GetDirectories(Root).Where(d => !System.IO.Path.GetFileName(d).StartsWith('.')));

        var subjects = paths.Where(Directory.Exists).Select(p => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ShadowSubject { Path = p }).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var s in subjects.Where(s => Repository.IsValid(s.Dir)))
        {
            using var repo = new Repository(s.Dir);
            s.RemoteUrl = repo.Network.Remotes["origin"]?.Url;
        }
        return subjects;
    }

    private static void Remember(IEnumerable<ShadowSubject> subjects)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SubjectsFile)!);
        File.WriteAllText(SubjectsFile, JsonSerializer.Serialize(subjects.Select(s => new ShadowSubject { Path = s.Path }).ToList()));
    }

    /// <summary>Cria a subpasta do assunto na pasta de assuntos (ou adota a que já existe).</summary>
    public static ShadowSubject CreateSubject(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.StartsWith('.') || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException(NxText.T("Nome de assunto inválido (não pode ter \\ / : * ? \" < > | nem começar com ponto).",
                "Invalid subject name (no \\ / : * ? \" < > | and it cannot start with a dot).",
                "Nombre de asunto no válido (sin \\ / : * ? \" < > | y no puede empezar con punto)."));
        var subject = new ShadowSubject { Path = System.IO.Path.Combine(Root, name) };
        Directory.CreateDirectory(subject.Path);
        Remember(LoadSubjects().Append(subject));
        return subject;
    }

    /// <summary>
    /// Snapshot da pasta do assunto (commit só se algo mudou desde o último). Só LÊ a pasta:
    /// índice e objetos ficam no histórico. Devolve a pasta do histórico (origem do worktree).
    /// </summary>
    public static Task<string> SnapshotAsync(ShadowSubject subject, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!Directory.Exists(subject.Path))
            throw new InvalidOperationException(NxText.T($"A pasta {subject.Path} não existe mais.", $"The folder {subject.Path} no longer exists.", $"La carpeta {subject.Path} ya no existe."));
        EnsureRepo(subject.Dir);
        // Histórico "bare" com a pasta do assunto como área de trabalho só durante o snapshot: nada é gravado nela.
        using var repo = new Repository(subject.Dir, new RepositoryOptions { WorkingDirectoryPath = subject.Path, IndexPath = System.IO.Path.Combine(subject.Dir, "index") });
        Commands.Stage(repo, "*");
        var tip = repo.Head.Tip;
        if (tip == null || repo.Diff.Compare<TreeChanges>(tip.Tree, DiffTargets.Index).Count > 0)
            repo.Commit("NXPilot · snapshot", GitWorktree.Author, GitWorktree.Author, new CommitOptions { AllowEmptyCommit = true });
        return subject.Dir;
    }, ct);

    private static void EnsureRepo(string dir)
    {
        if (Repository.IsValid(dir)) return;
        Repository.Init(dir, isBare: true);
        File.WriteAllText(System.IO.Path.Combine(dir, "info", "exclude"), DefaultExcludes);
        // Repositório vazio: HEAD aponta para um branch que ainda não existe — o nome que o GitHub usa por padrão.
        File.WriteAllText(System.IO.Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
        var history = new DirectoryInfo(System.IO.Path.GetDirectoryName(dir)!);
        history.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>
    /// Move o assunto (pasta + histórico) para dentro de <paramref name="newParent"/>. Só sem
    /// tarefa com worktree aberto: o worktree guarda o caminho do histórico.
    /// </summary>
    public static Task<ShadowSubject> MoveAsync(ShadowSubject subject, string newParent, CancellationToken ct = default) => Task.Run(() =>
    {
        var worktrees = System.IO.Path.Combine(subject.Dir, "worktrees");
        if (Directory.Exists(worktrees) && Directory.EnumerateDirectories(worktrees).Any())
            throw new InvalidOperationException(NxText.T("Este assunto ainda tem tarefas com worktree — remova-as no Histórico antes de mover.",
                "This subject still has tasks with a worktree — remove them in History before moving.",
                "Este asunto todavía tiene tareas con worktree — elimínelas en el Historial antes de mover."));
        var moved = new ShadowSubject { Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(newParent), subject.Name), RemoteUrl = subject.RemoteUrl };
        if (string.Equals(moved.Path, subject.Path, StringComparison.OrdinalIgnoreCase)) return subject;
        if (Directory.Exists(moved.Path) || Directory.Exists(moved.Dir))
            throw new InvalidOperationException(NxText.T($"Já existe {moved.Path} no destino.", $"{moved.Path} already exists at the destination.", $"Ya existe {moved.Path} en el destino."));

        var others = LoadSubjects().Where(s => !string.Equals(s.Path, subject.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Directory.Exists(subject.Dir))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(moved.Dir)!);
            MoveDirectory(subject.Dir, moved.Dir);
            new DirectoryInfo(System.IO.Path.GetDirectoryName(moved.Dir)!).Attributes |= FileAttributes.Hidden;
        }
        MoveDirectory(subject.Path, moved.Path);
        Remember(others.Append(moved));
        return moved;
    }, ct);

    /// <summary>Directory.Move não cruza unidades (ex.: C: → G: do Google Drive): copia e apaga.</summary>
    private static void MoveDirectory(string from, string to)
    {
        if (string.Equals(System.IO.Path.GetPathRoot(from), System.IO.Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, file)));
        GitWorktree.ForceDelete(from);
    }

    /// <summary>
    /// Leva para a pasta do assunto só o que a tarefa mudou desde <paramref name="baseCommit"/>.
    /// Arquivo que você também mudou na pasta depois do snapshot é conflito: sem
    /// <paramref name="overwriteConflicts"/>, nada é gravado e a lista volta para você decidir.
    /// </summary>
    public static Task<ApplyResult> ApplyAsync(string folder, string worktree, string baseCommit, bool overwriteConflicts = false, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(worktree);
        Commands.Stage(repo, "*"); // inclui o que ainda não foi commitado
        var baseTree = repo.Lookup<Commit>(baseCommit)?.Tree
            ?? throw new InvalidOperationException(NxText.T("Commit base da tarefa não encontrado.", "Task base commit not found.", "No se encontró el commit base de la tarea."));

        var writes = new List<string>();
        var deletes = new List<string>();
        foreach (var c in repo.Diff.Compare<TreeChanges>(baseTree, DiffTargets.Index))
        {
            switch (c.Status)
            {
                case ChangeKind.Deleted: deletes.Add(c.OldPath); break;
                case ChangeKind.Renamed: deletes.Add(c.OldPath); writes.Add(c.Path); break;
                case ChangeKind.Unmodified or ChangeKind.Ignored or ChangeKind.Untracked: break;
                default: writes.Add(c.Path); break;
            }
        }

        // Conflito = a pasta não está mais como no snapshot (e nem já está como a tarefa deixou).
        var conflicts = new List<string>();
        foreach (var rel in writes.Concat(deletes).Distinct())
        {
            var target = Local(folder, rel);
            byte[]? current = File.Exists(target) ? File.ReadAllBytes(target) : null;
            byte[]? original = baseTree[rel]?.Target is Blob b ? ReadBlob(b) : null;
            if (Same(current, original)) continue;
            var wanted = writes.Contains(rel) ? File.ReadAllBytes(Local(worktree, rel)) : null;
            if (!Same(current, wanted)) conflicts.Add(rel);
        }
        if (conflicts.Count > 0 && !overwriteConflicts) return new ApplyResult(0, 0, conflicts);

        var removed = deletes.Except(writes).ToList();
        foreach (var rel in removed)
            if (File.Exists(Local(folder, rel))) File.Delete(Local(folder, rel));
        foreach (var rel in writes)
        {
            var target = Local(folder, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(Local(worktree, rel), target, overwrite: true);
        }
        return new ApplyResult(writes.Count, removed.Count, conflicts);
    }, ct);

    /// <summary>
    /// Envia o histórico (main + branches das tarefas) para <paramref name="cloneUrl"/>. Quem
    /// chama já passou o push pelo gate. O token vai só na autenticação (nunca na URL nem no remoto salvo).
    /// </summary>
    public static Task PushAsync(string historyDir, string cloneUrl, string token, CancellationToken ct = default) => Task.Run(() =>
    {
        using var repo = new Repository(historyDir);
        if (repo.Network.Remotes["origin"] is { } origin) { if (origin.Url != cloneUrl) repo.Network.Remotes.Update("origin", r => r.Url = cloneUrl); }
        else repo.Network.Remotes.Add("origin", cloneUrl);
        var refspecs = repo.Branches.Where(b => !b.IsRemote).Select(b => $"{b.CanonicalName}:{b.CanonicalName}").ToList();
        try
        {
            repo.Network.Push(repo.Network.Remotes["origin"], refspecs, new PushOptions
            {
                CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = "x-access-token", Password = token },
            });
        }
        catch (LibGit2SharpException ex)
        {
            throw new InvalidOperationException("git push: " + (token.Length > 0 ? ex.Message.Replace(token, "***") : ex.Message));
        }
    }, ct);

    private static string Local(string root, string rel) => System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static byte[] ReadBlob(Blob blob)
    {
        using var s = blob.GetContentStream();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static bool Same(byte[]? a, byte[]? b) => a == null || b == null ? a == b : a.AsSpan().SequenceEqual(b);
}
