// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NXPilot.Integrations;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Work items atribuídos a mim, do Azure DevOps ou do GitHub Projects (somente leitura). A conexão
/// vem da extensão Work items ou, em branco lá, do NXProject; o projeto se escolhe aqui (começa no
/// último usado). "Criar tarefa" devolve o item escolhido; quem abre a Nova tarefa é a MainWindow.
/// </summary>
public partial class WorkItemsWindow : Window
{
    private const string DevOpsSource = "devops", GitHubSource = "github";

    private WorkItemSettings _settings = WorkItemSettings.Load();
    private bool _filling; // trocando as listas por código: não recarrega a cada item

    public WorkItem? Selected { get; private set; }

    /// <summary>Vínculo do item escolhido (DevOps: origem, projeto, id) — vai para a tarefa e para o prompt do agente.</summary>
    public WorkItemLink? SelectedLink { get; private set; }

    /// <summary>
    /// Linha da lista. Na hierarquia é um nó da árvore: recuo pelo nível e ▶/▼ para abrir os filhos
    /// (Project → EPIC/EPIC_GROUP → Feature → Story → Task).
    /// </summary>
    private sealed class ItemRow(WorkItem item, int depth = 0, double? remaining = null, int parentId = 0) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public WorkItem Item { get; } = item;
        public int Depth { get; } = depth;
        public double? Remaining { get; } = remaining;
        public int ParentId { get; } = parentId;
        public ItemRow? Parent { get; set; }
        public List<ItemRow> Children { get; } = new();
        public int Id => Item.Id;
        public string Type => Item.Type;
        public string Title => Item.Title;
        public string State => Item.State;
        public string AssignedTo => Item.AssignedTo;
        public Thickness Indent => new(Depth * 18, 0, 0, 0);
        public bool HasChildren => Children.Count > 0;

        private bool _expanded;
        public bool Expanded
        {
            get => _expanded;
            set { if (SetProperty(ref _expanded, value)) OnPropertyChanged(nameof(Glyph)); }
        }

        public string Glyph => !HasChildren ? "" : Expanded ? "▼" : "▶";

        /// <summary>Visível quando todos os pais estão abertos.</summary>
        public bool Visible => Parent == null || (Parent.Expanded && Parent.Visible);

        /// <summary>Quantos itens há abaixo (todos os níveis) — mostrado ao lado do título fechado.</summary>
        public int Descendants => Children.Sum(c => 1 + c.Descendants);
        public string CountText => HasChildren ? $" ({Descendants})" : "";
    }

    private List<ItemRow> _allRows = new();

    /// <summary>Monta a árvore (pai pelo id) e abre só o Project: aparecem os EPICs / EPIC_GROUPs.</summary>
    private static List<ItemRow> LinkTree(List<ItemRow> rows)
    {
        var byId = new Dictionary<int, ItemRow>();
        foreach (var r in rows) byId.TryAdd(r.Id, r);
        foreach (var r in rows)
            if (r.ParentId != 0 && byId.TryGetValue(r.ParentId, out var p) && !ReferenceEquals(p, r)) { r.Parent = p; p.Children.Add(r); }
        foreach (var r in rows) r.Expanded = r.Parent == null; // raiz aberta; o resto fechado
        return rows;
    }

    /// <summary>Lista na tela = só as linhas cujos pais estão abertos (mantém a ordem da árvore e a seleção).</summary>
    private void ShowRows(ItemRow? select = null)
    {
        var visible = _allRows.Where(r => r.Visible).ToList();
        Grid.ItemsSource = visible;
        var hasTree = _allRows.Any(r => r.HasChildren);
        ExpandAllButton.Visibility = CollapseAllButton.Visibility = hasTree ? Visibility.Visible : Visibility.Collapsed;
        if (select != null && visible.Contains(select)) { Grid.SelectedItem = select; Grid.ScrollIntoView(select); }
        else if (visible.Count > 0) Grid.SelectedIndex = 0;
    }

    private void Toggle(ItemRow row, bool? expand = null)
    {
        if (!row.HasChildren) return;
        row.Expanded = expand ?? !row.Expanded;
        ShowRows(row);
    }

    private void OnToggleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ItemRow row) { Toggle(row); e.Handled = true; }
    }

    /// <summary>Setas: → abre, ← fecha (ou sobe para o pai).</summary>
    private void OnGridKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Grid.SelectedItem is not ItemRow row) return;
        if (e.Key == System.Windows.Input.Key.Right && row.HasChildren && !row.Expanded) { Toggle(row, true); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Left)
        {
            if (row.HasChildren && row.Expanded) Toggle(row, false);
            else if (row.Parent != null) ShowRows(row.Parent);
            e.Handled = true;
        }
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _allRows) r.Expanded = true;
        ShowRows(Grid.SelectedItem as ItemRow);
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _allRows) r.Expanded = r.Parent == null;
        ShowRows(_allRows.FirstOrDefault());
    }

    private const string MineScope = "mine", TreeScope = "tree";
    private string Scope => (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? MineScope;

    public WorkItemsWindow()
    {
        InitializeComponent();
        Title = Heading.Text = "Work items";
        ColId.Header = "ID";
        ColType.Header = T("Tipo", "Type", "Tipo");
        ColTitle.Header = T("Título", "Title", "Título");
        ColState.Header = T("Estado", "State", "Estado");
        ColAssigned.Header = T("Responsável", "Assigned to", "Responsable");
        DetailCriteriaLabel.Text = T("CRITÉRIOS DE ACEITE", "ACCEPTANCE CRITERIA", "CRITERIOS DE ACEPTACIÓN");
        SourceBox.ToolTip = T("Origem", "Source", "Origen");
        ProjectBox.ToolTip = T("Projeto", "Project", "Proyecto");
        ConfigureButton.Content = T("Configurar…", "Configure…", "Configurar…");
        RefreshButton.Content = T("Atualizar", "Refresh", "Actualizar");
        CreateButton.Content = T("Criar tarefa a partir deste item ▶", "Create task from this item ▶", "Crear tarea a partir de este elemento ▶");
        CloseButton.Content = T("Fechar", "Close", "Cerrar");
        ExpandAllButton.Content = T("⊞ Expandir tudo", "⊞ Expand all", "⊞ Expandir todo");
        CollapseAllButton.Content = T("⊟ Recolher", "⊟ Collapse", "⊟ Contraer");
        ScopeBox.Items.Add(new ComboBoxItem { Content = T("Só as minhas · abertas (Story, Bug, Task)", "Only mine · open (Story, Bug, Task)", "Solo las mías · abiertas (Story, Bug, Task)"), Tag = MineScope });
        ScopeBox.Items.Add(new ComboBoxItem { Content = T("Todas as abertas · hierarquia do Work Item Project", "All open · Project work item hierarchy", "Todas las abiertas · jerarquía del Work Item Project"), Tag = TreeScope });
        ScopeBox.ToolTip = T("Hierarquia: o Work Item Project escolhido e tudo o que está pendente abaixo dele (Epic, Feature, Story, Task) — de qualquer pessoa. Qualquer um vira a tarefa, com o vínculo.",
            "Hierarchy: the chosen Project work item and everything pending below it (Epic, Feature, Story, Task) — anyone's. Any of them becomes the task, with the link.",
            "Jerarquía: el Work Item Project elegido y todo lo pendiente debajo (Epic, Feature, Story, Task) — de cualquier persona. Cualquiera se convierte en la tarea, con el vínculo.");
        _filling = true;
        ScopeBox.SelectedIndex = _settings.LastDevOpsScope == TreeScope ? 1 : 0;
        _filling = false;
        Loaded += async (_, _) => await ReloadAllAsync();
    }

    private string? Source => (SourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
    private object? ProjectTag => (ProjectBox.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>Recarrega tudo (ao abrir e ao voltar do Configurar): origens, projetos e itens.</summary>
    private async Task ReloadAllAsync()
    {
        _settings = WorkItemSettings.Load();
        _filling = true;
        SourceBox.Items.Clear();
        if (_settings.DevOps() != null) SourceBox.Items.Add(new ComboBoxItem { Content = "Azure DevOps", Tag = DevOpsSource });
        if (_settings.GitHub() != null) SourceBox.Items.Add(new ComboBoxItem { Content = "GitHub Projects", Tag = GitHubSource });
        SourceBox.SelectedItem = SourceBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.LastSource)
                                 ?? SourceBox.Items.Cast<ComboBoxItem>().FirstOrDefault();
        SourceBox.Visibility = ProjectBox.Visibility = SourceBox.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _filling = false;
        await LoadProjectsAsync();
    }

    private async void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (Source is { } source) Remember(s => s.LastSource = source);
        await LoadProjectsAsync();
    }

    private async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        switch (ProjectTag)
        {
            case string name: Remember(s => s.LastDevOpsProject = name); break;
            case int number: Remember(s => s.LastGitHubProjectNumber = number); break;
        }
        await LoadRootsAsync();
    }

    private int RootId => (RootBox.SelectedItem as ComboBoxItem)?.Tag is int id ? id : 0;

    private async void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        Remember(s => s.LastDevOpsScope = Scope);
        await LoadItemsAsync();
    }

    private async void OnRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        var id = RootId;
        if (Source == GitHubSource) Remember(s => s.LastGitHubRootIssue = id);
        else Remember(s => s.LastDevOpsRootId = id);
        await LoadItemsAsync();
    }

    /// <summary>
    /// Azure DevOps: os work items do nível Project (o projeto executado) do Team Project escolhido.
    /// Começa no último escolhido ou, na primeira vez, na raiz configurada no NXProject.
    /// </summary>
    private async Task LoadRootsAsync()
    {
        _filling = true;
        RootBox.Items.Clear();
        RootBox.Visibility = Visibility.Collapsed;
        ScopeBox.Visibility = Source == DevOpsSource ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (Source == DevOpsSource && _settings.DevOps() is { } ado && ProjectTag is string project)
            {
                RootBox.Visibility = Visibility.Visible;
                RootBox.Items.Add(new ComboBoxItem { Content = T("Todos os work items Project", "All Project work items", "Todos los work items Project"), Tag = 0 });
                try
                {
                    using var client = new AzureDevOpsClient(ado.Connection with { Project = project });
                    foreach (var (id, title) in await client.ListProjectWorkItemsAsync(NXProjectConfig.ProjectLevelTypes()))
                        RootBox.Items.Add(new ComboBoxItem { Content = $"#{id} · {title}", Tag = id });
                }
                catch (Exception ex) { StatusText.Text = ex.Message; }
                var nxRoot = NXProjectConfig.RootWorkItem() is { } r && string.Equals(r.TeamProject, project, StringComparison.OrdinalIgnoreCase) ? r.RootId : 0;
                var want = _settings.LastDevOpsRootId ?? nxRoot;
                RootBox.SelectedItem = RootBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == want)
                                       ?? RootBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == nxRoot)
                                       ?? RootBox.Items[0];
            }
            else if (Source == GitHubSource && _settings.GitHub() is { } gh && ProjectTag is int board)
            {
                // GitHub: a issue raiz (nível Project) — pelo tipo, pelo campo de nível ou pelo topo da árvore.
                RootBox.Visibility = Visibility.Visible;
                RootBox.Items.Add(new ComboBoxItem { Content = T("Todas as issues Project", "All Project issues", "Todas las issues Project"), Tag = 0 });
                try
                {
                    foreach (var (number, title) in await Board(gh.Connection with { Number = board }).ListRootIssuesAsync(NXProjectConfig.ProjectLevelTypes()))
                        RootBox.Items.Add(new ComboBoxItem { Content = $"#{number} · {title}", Tag = number });
                }
                catch (Exception ex) { StatusText.Text = ex.Message; }
                var nxRoot = NXProjectConfig.GitRootIssue() is { } r && string.Equals(r.Owner, gh.Connection.Owner, StringComparison.OrdinalIgnoreCase)
                             && (r.Board == 0 || r.Board == board) ? r.RootIssue : 0;
                var want = _settings.LastGitHubRootIssue ?? nxRoot;
                RootBox.SelectedItem = RootBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == want)
                                       ?? RootBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == nxRoot)
                                       ?? RootBox.Items[0];
            }
        }
        finally { _filling = false; }
        await LoadItemsAsync();
    }

    /// <summary>Projeto DevOps da lista atual (vínculo da tarefa); nulo no GitHub.</summary>
    private string? _linkProject;

    private GitHubProjectsClient? _board;

    /// <summary>
    /// Cliente do board do GitHub, reaproveitado entre a lista de raízes e a de itens (o board é
    /// lido uma vez só). Outro board ou Atualizar descartam o anterior.
    /// </summary>
    private GitHubProjectsClient Board(GitHubProjectConnection connection)
    {
        if (_board is { } current && _boardConnection == connection) return current;
        _board?.Dispose();
        _boardConnection = connection;
        return _board = new GitHubProjectsClient(connection, levelField: NXProjectConfig.GitLevelField());
    }

    private GitHubProjectConnection? _boardConnection;

    protected override void OnClosed(EventArgs e)
    {
        _board?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Guarda a escolha da tela (origem/projeto) para a próxima vez.</summary>
    private void Remember(Action<WorkItemSettings> change)
    {
        change(_settings);
        try { _settings.Save(); } catch { /* só a preferência da tela */ }
    }

    /// <summary>Projetos da origem escolhida; seleciona o último usado ou o da conexão. Falhou: fica só o da conexão.</summary>
    private async Task LoadProjectsAsync()
    {
        _filling = true;
        ProjectBox.Items.Clear();
        try
        {
            switch (Source)
            {
                case DevOpsSource when _settings.DevOps() is { } ado:
                {
                    IReadOnlyList<string> names;
                    try { using var client = new AzureDevOpsClient(ado.Connection); names = await client.ListProjectsAsync(); }
                    catch (Exception ex) { names = Array.Empty<string>(); StatusText.Text = ex.Message; }
                    if (!names.Contains(ado.Connection.Project, StringComparer.OrdinalIgnoreCase)) names = names.Prepend(ado.Connection.Project).ToList();
                    foreach (var n in names) ProjectBox.Items.Add(new ComboBoxItem { Content = n, Tag = n });
                    var want = _settings.LastDevOpsProject.Length > 0 ? _settings.LastDevOpsProject : ado.Connection.Project;
                    ProjectBox.SelectedItem = ProjectBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, want, StringComparison.OrdinalIgnoreCase))
                                              ?? ProjectBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == ado.Connection.Project);
                    break;
                }
                case GitHubSource when _settings.GitHub() is { } gh:
                {
                    IReadOnlyList<(int Number, string Title)> projects;
                    try { using var client = new GitHubProjectsClient(gh.Connection); projects = await client.ListProjectsAsync(); }
                    catch (Exception ex) { projects = Array.Empty<(int, string)>(); StatusText.Text = ex.Message; }
                    if (projects.All(p => p.Number != gh.Connection.Number)) projects = projects.Prepend((gh.Connection.Number, "")).ToList();
                    foreach (var (number, title) in projects)
                        ProjectBox.Items.Add(new ComboBoxItem { Content = title.Length > 0 ? $"#{number} · {title}" : $"#{number}", Tag = number });
                    var want = _settings.LastGitHubProjectNumber > 0 ? _settings.LastGitHubProjectNumber : gh.Connection.Number;
                    ProjectBox.SelectedItem = ProjectBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == want)
                                              ?? ProjectBox.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == gh.Connection.Number);
                    break;
                }
            }
        }
        finally { _filling = false; }
        await LoadRootsAsync();
    }

    private async Task LoadItemsAsync()
    {
        Grid.ItemsSource = null;
        _allRows = new();
        CreateButton.IsEnabled = OpenWebButton.IsEnabled = false;
        RefreshButton.IsEnabled = Source != null;
        OnSelectionChanged(this, null!);
        Heading.Text = "Work items";
        string Via(WorkItemConnectionOrigin o) => o == WorkItemConnectionOrigin.Own
            ? T("extensão Work items", "Work items extension", "extensión Work items")
            : T("conexão do NXProject", "NXProject connection", "conexión de NXProject");
        try
        {
            IReadOnlyList<ItemRow> items;
            switch (Source)
            {
                case DevOpsSource when _settings.DevOps() is { } ado && ProjectTag is string project:
                {
                    var root = RootBox.SelectedItem is ComboBoxItem { Tag: int id and > 0 } rootItem ? (Id: id, Name: rootItem.Content as string) : (Id: 0, Name: null);
                    RootBox.ToolTip = root.Name ?? T("Work item Project (o projeto executado)", "Project work item (the project being run)", "Work item Project (el proyecto ejecutado)"); // nome longo fica cortado na caixa
                    var where = root.Name ?? project;
                    SubHeading.Text = T($"Itens abertos atribuídos a você em {where} · {Via(ado.Origin)}, somente leitura.",
                                        $"Open items assigned to you in {where} · {Via(ado.Origin)}, read-only.",
                                        $"Elementos abiertos asignados a usted en {where} · {Via(ado.Origin)}, solo lectura.");
                    OpenWebButton.Content = T("Abrir no DevOps", "Open in DevOps", "Abrir en DevOps");
                    StatusText.Text = T("Consultando o Azure DevOps…", "Querying Azure DevOps…", "Consultando Azure DevOps…");
                    using var client = new AzureDevOpsClient(ado.Connection with { Project = project });
                    if (Scope == TreeScope)
                    {
                        if (root.Id == 0)
                        {
                            SubHeading.Text = T("Escolha um Work Item Project (à direita) para ver a hierarquia dele.", "Choose a Project work item (on the right) to see its hierarchy.", "Elija un Work Item Project (a la derecha) para ver su jerarquía.");
                            StatusText.Text = "";
                            return;
                        }
                        SubHeading.Text = T($"{where}: o item e tudo o que está pendente abaixo dele, de qualquer pessoa · {Via(ado.Origin)}, somente leitura. A tarefa fica vinculada ao item escolhido.",
                                            $"{where}: the item and everything pending below it, anyone's · {Via(ado.Origin)}, read-only. The task stays linked to the chosen item.",
                                            $"{where}: el elemento y todo lo pendiente debajo, de cualquier persona · {Via(ado.Origin)}, solo lectura. La tarea queda vinculada al elemento elegido.");
                        items = (await client.TreeAsync(root.Id, openOnly: true, max: 1000))
                            .Select(n => new ItemRow(n.ToWorkItem(), n.Depth, n.RemainingWork, n.ParentId)).ToList();
                    }
                    else
                    {
                        // Work item Project escolhido: só o que está debaixo dele (Epic → Feature → Story → Task).
                        var within = root.Id > 0 ? await client.DescendantIdsAsync(root.Id) : null;
                        items = (await client.MyOpenWorkItemsAsync(within: within)).Select(w => new ItemRow(w)).ToList();
                    }
                    _linkProject = project;
                    break;
                }
                case GitHubSource when _settings.GitHub() is { } gh && ProjectTag is int number:
                {
                    var root = RootBox.SelectedItem is ComboBoxItem { Tag: int id and > 0 } rootItem ? (Id: id, Name: rootItem.Content as string) : (Id: 0, Name: null);
                    RootBox.ToolTip = root.Name ?? T("Issue Project (o projeto executado)", "Project issue (the project being run)", "Issue Project (el proyecto ejecutado)");
                    var where = root.Name ?? T($"{gh.Connection.Owner} · projeto {number}", $"{gh.Connection.Owner} · project {number}", $"{gh.Connection.Owner} · proyecto {number}");
                    SubHeading.Text = T($"Issues abertas e rascunhos atribuídos a você em {where} · {Via(gh.Origin)}, somente leitura.",
                                        $"Open issues and drafts assigned to you in {where} · {Via(gh.Origin)}, read-only.",
                                        $"Issues abiertas y borradores asignados a usted en {where} · {Via(gh.Origin)}, solo lectura.");
                    OpenWebButton.Content = T("Abrir no GitHub", "Open in GitHub", "Abrir en GitHub");
                    StatusText.Text = T("Consultando o GitHub Projects…", "Querying GitHub Projects…", "Consultando GitHub Projects…");
                    var client = Board(gh.Connection with { Number = number });
                    items = (await client.MyOpenItemsAsync(rootIssue: root.Id)).Select(w => new ItemRow(w)).ToList(); // Issue Project escolhida: só as sub-issues dela
                    _linkProject = null;
                    if (client.ProjectTitle is { Length: > 0 } title) Heading.Text = $"Work items · {title}";
                    break;
                }
                default:
                    SubHeading.Text = T("Nenhuma origem configurada. Configure o Azure DevOps ou o GitHub Projects na extensão Work items (ou no NXProject — o NXPilot usa a mesma conexão).",
                                        "No source configured. Set up Azure DevOps or GitHub Projects in the Work items extension (or in NXProject — NXPilot uses the same connection).",
                                        "Ningún origen configurado. Configure Azure DevOps o GitHub Projects en la extensión Work items (o en NXProject — NXPilot usa la misma conexión).");
                    OpenWebButton.Content = T("Abrir no navegador", "Open in browser", "Abrir en el navegador");
                    StatusText.Text = "";
                    return;
            }
            _allRows = LinkTree(items.ToList());
            ShowRows();
            StatusText.Text = T($"{items.Count} item(ns).", $"{items.Count} item(s).", $"{items.Count} elemento(s).")
                + (_allRows.Any(r => r.HasChildren) ? T(" ▶ abre os filhos (ou seta →); ← fecha.", " ▶ opens the children (or → key); ← closes.", " ▶ abre los hijos (o flecha →); ← cierra.") : "");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Grid.SelectedItem as ItemRow;
        var w = row?.Item;
        CreateButton.IsEnabled = OpenWebButton.IsEnabled = w != null;
        DetailTitle.Text = w == null ? "" : (w.Id > 0 ? $"{w.Type} #{w.Id} · {w.Title}" : w.Title)
            + (row is { AssignedTo.Length: > 0 } ? $"\n{row.AssignedTo}" : "") + (row?.Remaining is { } h ? T($" · {h:0.#} h restantes", $" · {h:0.#} h remaining", $" · {h:0.#} h restantes") : "");
        DetailDescription.Text = w == null ? "" : w.Description.Length > 0 ? w.Description : T("(sem descrição)", "(no description)", "(sin descripción)");
        DetailCriteria.Text = w == null ? "" : w.AcceptanceCriteria.Length > 0 ? w.AcceptanceCriteria : T("(sem critérios de aceite)", "(no acceptance criteria)", "(sin criterios de aceptación)");
    }

    /// <summary>Atualizar: relê do servidor (o board do GitHub fica em memória até aqui).</summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _board?.Dispose();
        _board = null;
        await LoadRootsAsync();
    }

    /// <summary>Abre a extensão Work items; ao voltar, recarrega (a conexão pode ter mudado).</summary>
    private async void OnConfigureClick(object sender, RoutedEventArgs e)
    {
        new ExtensionsWindow("workitems") { Owner = this }.ShowDialog();
        await ReloadAllAsync();
    }

    private void OnOpenWebClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ItemRow { Item: var w }) Process.Start(new ProcessStartInfo(w.Url) { UseShellExecute = true });
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ItemRow { Item: var w }) return;
        Selected = w;
        SelectedLink = _linkProject is { } project && w.Id > 0 ? new WorkItemLink("Azure DevOps", project, w.Id, w.Type, w.Title, w.Url) : null;
        Close();
    }
}
