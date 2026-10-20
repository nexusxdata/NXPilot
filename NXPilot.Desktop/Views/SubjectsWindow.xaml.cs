// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NXPilot.Agents;
using NXPilot.Desktop.Services;
using NXPilot.GitRepos;
using NXPilot.Integrations;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Assuntos do versionamento local: cada um é uma subpasta da pasta de assuntos (com o
/// histórico oculto ao lado). Aqui se escolhe a pasta de assuntos (ex.: OneDrive), se move um
/// assunto para outra pasta ou para o GitHub — criar o repositório e enviar passam pelo gate
/// (push na main = ALTO).
/// </summary>
public partial class SubjectsWindow : Window
{
    /// <summary>Assunto escolhido ao fechar (a Nova tarefa já volta com ele selecionado).</summary>
    public string? SelectedName { get; private set; }

    public SubjectsWindow(string? select = null)
    {
        InitializeComponent();
        RefreshRoot();
        RefreshList(select);
    }

    private ShadowSubject? Selected => SubjectList.SelectedItem as ShadowSubject;

    private void RefreshRoot()
    {
        RootText.Text = ShadowRepo.Root;
        RootText.ToolTip = ShadowRepo.Root;
        DefaultRootButton.IsEnabled = !string.Equals(ShadowRepo.Root, ShadowRepo.DefaultRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshList(string? select)
    {
        var subjects = ShadowRepo.LoadSubjects();
        SubjectList.ItemsSource = subjects;
        SubjectList.SelectedItem = subjects.FirstOrDefault(s => s.Name == select) ?? subjects.FirstOrDefault();
        OnSubjectChanged(this, null!);
    }

    private void OnSubjectChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var s = Selected;
        SelectedName = s?.Name;
        DetailPanel.Visibility = s == null ? Visibility.Hidden : Visibility.Visible;
        if (s == null) return;
        SubjectName.Text = s.Name;
        SubjectPath.Text = s.Path;
        var moved = s.RemoteUrl != null;
        RepoNameBox.Visibility = RepoNameLabel.Visibility = moved ? Visibility.Collapsed : Visibility.Visible;
        RepoNameBox.Text = RepoSlug(s.Name);
        GitHubButton.Content = moved ? T("Enviar ao GitHub", "Push to GitHub", "Enviar a GitHub") : T("Mover para o GitHub…", "Move to GitHub…", "Mover a GitHub…");
        GitHubText.Text = moved
            ? T($"Este assunto está no GitHub: {s.WebUrl}. \"Enviar\" leva os snapshots e os branches das tarefas novas.",
                $"This subject is on GitHub: {s.WebUrl}. \"Push\" sends the snapshots and the new task branches.",
                $"Este asunto está en GitHub: {s.WebUrl}. \"Enviar\" lleva los snapshots y las ramas de las tareas nuevas.")
            : T("Opcional: cria um repositório PRIVADO na sua conta (token da Extensão MCP) e envia todo o histórico do assunto. Precisa da sua aprovação.",
                "Optional: creates a PRIVATE repository in your account (token from MCP Extension) and sends the subject's whole history. Requires your approval.",
                "Opcional: crea un repositorio PRIVADO en su cuenta (token de Extensión MCP) y envía todo el historial del asunto. Requiere su aprobación.");
    }

    private void OnNewNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnCreateClick(sender, e); e.Handled = true; }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var subject = ShadowRepo.CreateSubject(NewNameBox.Text);
            NewNameBox.Text = "";
            StatusText.Text = T($"Assunto criado: coloque os arquivos em {subject.Path}.", $"Subject created: put the files in {subject.Path}.", $"Asunto creado: ponga los archivos en {subject.Path}.");
            RefreshList(subject.Name);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s && Directory.Exists(s.Path)) Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true });
    }

    /// <summary>Leva a pasta do assunto e o histórico dele para dentro de outra pasta (ex.: do disco para o OneDrive).</summary>
    private async void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var dialog = new OpenFolderDialog { Title = T($"Mover \"{s.Name}\" para dentro de…", $"Move \"{s.Name}\" into…", $"Mover \"{s.Name}\" dentro de…") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StatusText.Text = T("Movendo…", "Moving…", "Moviendo…");
            var moved = await ShadowRepo.MoveAsync(s, dialog.FolderName);
            StatusText.Text = T($"Movido para {moved.Path}.", $"Moved to {moved.Path}.", $"Movido a {moved.Path}.");
            RefreshList(moved.Name);
        }
        catch (Exception ex) { StatusText.Text = T("Não foi possível mover: ", "Could not move: ", "No se pudo mover: ") + ex.Message; }
    }

    private void OnChangeRootClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Pasta dos assuntos", "Subjects folder", "Carpeta de los asuntos") };
        if (dialog.ShowDialog(this) == true) SetRoot(Path.Combine(Path.GetFullPath(dialog.FolderName), "NXPilot"));
    }

    private void OnDefaultRootClick(object sender, RoutedEventArgs e) => SetRoot(null);

    private void SetRoot(string? root)
    {
        AppSettings.Update(s => s.SubjectsRoot = root);
        ShadowRepo.Root = root ?? "";
        RefreshRoot();
        RefreshList(SelectedName);
        StatusText.Text = T("Assuntos novos nascem nessa pasta; os que já existem continuam onde estão (use \"Mover para outra pasta\").",
                            "New subjects are created in this folder; existing ones stay where they are (use \"Move to another folder\").",
                            "Los asuntos nuevos se crean en esa carpeta; los existentes se quedan donde están (use \"Mover a otra carpeta\").");
    }

    /// <summary>
    /// Mover para o GitHub: sua aprovação (gate) → cria o repositório privado (só na 1ª vez) →
    /// snapshot → envia main + branches das tarefas. Depois disso, o botão só reenvia.
    /// </summary>
    private async void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var token = GitRepoSettings.Load().GitHubToken;
        if (token.Length == 0)
        {
            StatusText.Text = T("Configure o token do GitHub na Extensão MCP (para criar o repositório, o token fine-grained precisa da permissão Administration: escrita).",
                                "Set the GitHub token in MCP Extension (to create the repository, a fine-grained token needs the Administration: write permission).",
                                "Configure el token de GitHub en Extensión MCP (para crear el repositorio, el token fine-grained necesita el permiso Administration: escritura).");
            return;
        }
        var repoName = s.RemoteUrl == null ? RepoNameBox.Text.Trim() : null;
        if (repoName is { Length: 0 }) { StatusText.Text = T("Dê um nome ao repositório.", "Name the repository.", "Dé un nombre al repositorio."); return; }

        var review = new GitOperationReview(GitOperationKind.Push, s.WebUrl ?? $"github.com/<você>/{repoName}", "main", TargetsDefaultBranch: true,
            T($"assunto \"{s.Name}\": snapshots + branches das tarefas{(s.RemoteUrl == null ? " · cria o repositório privado" : "")}",
              $"subject \"{s.Name}\": snapshots + task branches{(s.RemoteUrl == null ? " · creates the private repository" : "")}",
              $"asunto \"{s.Name}\": snapshots + ramas de las tareas{(s.RemoteUrl == null ? " · crea el repositorio privado" : "")}"));
        var approver = new GateGitApprover((r, risk, _) => Task.FromResult(MessageBox.Show(this,
            T($"{r.Summary}\n\nRisco: {risk.Level}. Enviar para o GitHub?", $"{r.Summary}\n\nRisk: {risk.Level}. Send to GitHub?", $"{r.Summary}\n\nRiesgo: {risk.Level}. ¿Enviar a GitHub?"),
            "NXPilot · Gate", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes));
        var approval = await approver.ApproveAsync(review);
        if (!approval.Approved) { StatusText.Text = T("Envio negado: ", "Push denied: ", "Envío denegado: ") + approval.Reason; return; }

        GitHubButton.IsEnabled = false;
        try
        {
            var cloneUrl = s.RemoteUrl;
            await ShadowRepo.SnapshotAsync(s);
            if (cloneUrl == null)
            {
                StatusText.Text = T("Criando o repositório no GitHub…", "Creating the repository on GitHub…", "Creando el repositorio en GitHub…");
                using var api = new GitHubApi(token);
                cloneUrl = (await api.CreateRepositoryAsync(repoName!, $"NXPilot · {s.Name}")).CloneUrl;
            }
            StatusText.Text = T("Enviando o histórico…", "Sending the history…", "Enviando el historial…");
            await ShadowRepo.PushAsync(s.Dir, cloneUrl, token); // grava o remoto no histórico: o próximo clique reenvia para o mesmo
            var web = cloneUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? cloneUrl[..^4] : cloneUrl;
            StatusText.Text = T($"Enviado: {web}", $"Sent: {web}", $"Enviado: {web}");
            Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = T("Não foi possível enviar: ", "Could not push: ", "No se pudo enviar: ") + ex.Message; }
        finally
        {
            GitHubButton.IsEnabled = true;
            RefreshList(s.Name);
        }
    }

    /// <summary>Nome de repositório aceito pelo GitHub: sem acentos, só letras, números, ".", "-" e "_".</summary>
    private static string RepoSlug(string name)
    {
        var plain = new string(name.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var slug = new string(plain.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "assunto";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
