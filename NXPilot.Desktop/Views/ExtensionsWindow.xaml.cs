// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NXPilot.Databricks;
using NXPilot.GitRepos;
using NXPilot.Integrations;
using NXPilot.Mail;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Extensões: Databricks SQL, Git (GitHub/Azure DevOps), Work items (Azure DevOps/GitHub Projects) e E-mail (Outlook/Gmail). Os testes usam
/// o que está na tela (sem salvar); o Salvar grava as três configurações. Token em branco = mantém.
/// </summary>
public partial class ExtensionsWindow : Window
{
    private readonly DatabricksSettings _db;
    private readonly GitRepoSettings _git;
    private readonly MailSettings _mail;
    private readonly WorkItemSettings _wi;
    private readonly TeamsSettings _teams;
    private readonly Services.WindowsUiSettings _windows;

    public ExtensionsWindow(string? tab = null)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _db = DatabricksSettings.Load();
        _git = GitRepoSettings.Load();
        _mail = MailSettings.Load();
        _wi = WorkItemSettings.Load();
        _teams = TeamsSettings.Load();
        TeamsDecisionsCheck.IsChecked = _teams.SendDecisions;
        TeamsTasksCheck.IsChecked = _teams.SendTasks;
        TeamsFailuresCheck.IsChecked = _teams.SendFailures;
        TeamsMessagesCheck.IsChecked = _teams.SendMessages;
        foreach (var subject in NXPilot.Agents.ShadowRepo.LoadSubjects())
            TeamsSubjectsPanel.Children.Add(new CheckBox
            {
                Content = subject.GroupedName, Tag = subject.Path, ToolTip = subject.Path, Margin = new Thickness(0, 0, 0, 3),
                IsChecked = _teams.Subjects.Any(s => string.Equals(s, subject.Path, StringComparison.OrdinalIgnoreCase)),
            });
        if (TeamsSubjectsPanel.Children.Count == 0)
            TeamsSubjectsPanel.Children.Add(new TextBlock { Text = T("Nenhum assunto ainda.", "No subjects yet.", "Aún no hay asuntos."), Foreground = Brushes.Gray });
        (_teams.Subjects.Count == 0 ? TeamsAllSubjects : TeamsSomeSubjects).IsChecked = true;
        WiAdoOrgBox.Text = _wi.DevOpsOrganizationUrl;
        WiAdoProjectBox.Text = _wi.DevOpsProject;
        WiGhOwnerBox.Text = _wi.GitHubOwner;
        WiGhOrgCheck.IsChecked = _wi.GitHubOwnerIsOrganization;
        WiGhNumberBox.Text = _wi.GitHubProjectNumber > 0 ? _wi.GitHubProjectNumber.ToString() : "";
        _windows = Services.WindowsUiSettings.Load();
        AllowedAppsBox.Text = string.Join(Environment.NewLine, _windows.AllowedProcesses);

        DbHostBox.Text = _db.Host;
        DbWarehouseBox.Text = _db.WarehouseId;
        DbCatalogBox.Text = _db.Catalog;
        DbSchemaBox.Text = _db.Schema;
        AdoOrgBox.Text = _git.AzureDevOpsOrganizationUrl;
        OutlookClientBox.Text = _mail.OutlookClientId;
        OutlookTenantBox.Text = _mail.OutlookTenant;
        GmailClientBox.Text = _mail.GmailClientId;

        Tabs.SelectedItem = tab?.ToLowerInvariant() switch
        {
            "git" => GitTab,
            "workitems" or "work-items" => WorkItemsTab,
            "teams" => TeamsTab,
            "mail" or "email" => MailTab,
            "powerbi" => PowerBiTab,
            "windows" or "janelas" => WindowsTab,
            _ => DatabricksTab,
        };
        BuildExtensionCombo();
        Refresh();
    }

    // ------------------------------------------------------------ escolha da extensão

    /// <summary>
    /// Combo com as extensões (ordem alfabética), cada item ligado à bolinha e ao nome da aba dela —
    /// o status atualiza junto e o nome acompanha o idioma.
    /// </summary>
    private void BuildExtensionCombo()
    {
        var items = new List<ComboBoxItem>();
        foreach (var tab in Tabs.Items.OfType<TabItem>())
        {
            var header = tab.Header as StackPanel;
            var dot = header?.Children.OfType<System.Windows.Shapes.Ellipse>().FirstOrDefault();
            var name = header?.Children.OfType<TextBlock>().FirstOrDefault();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var copyDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (dot != null) copyDot.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding(nameof(System.Windows.Shapes.Shape.Fill)) { Source = dot });
            var copyName = new TextBlock();
            if (name != null) copyName.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = name });
            panel.Children.Add(copyDot);
            panel.Children.Add(copyName);
            items.Add(new ComboBoxItem { Content = panel, Tag = tab });
        }
        foreach (var item in items.OrderBy(i => ((i.Tag as TabItem)?.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text, StringComparer.CurrentCultureIgnoreCase))
            ExtensionBox.Items.Add(item);
        SyncCombo();
    }

    private void OnExtensionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((ExtensionBox.SelectedItem as ComboBoxItem)?.Tag is TabItem tab && !ReferenceEquals(Tabs.SelectedItem, tab)) Tabs.SelectedItem = tab;
    }

    /// <summary>Aba trocada pelo código (ex.: abrir já em Work items): a combo acompanha. Ignora as combos de dentro das abas.</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs)) SyncCombo();
    }

    private void SyncCombo()
    {
        if (ExtensionBox == null) return; // durante o InitializeComponent
        ExtensionBox.SelectedItem = ExtensionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, Tabs.SelectedItem));
    }

    // ------------------------------------------------------------ estado

    private void Refresh()
    {
        string Saved(string value) => value.Length > 0 ? T("Salvo (cifrado). Deixe em branco para manter.", "Saved (encrypted). Leave blank to keep it.", "Guardado (cifrado). Déjelo en blanco para mantenerlo.") : "";
        DbTokenState.Text = Saved(_db.Token);
        GhTokenState.Text = Saved(_git.GitHubToken);
        AdoPatState.Text = _git.AzureDevOpsFromNXProject
            ? T("Usando o PAT do NXProject (não é copiado para o NXPilot). Ele precisa do escopo Code para repositórios.",
                "Using the NXProject PAT (not copied into NXPilot). It needs the Code scope for repositories.",
                "Usando el PAT de NXProject (no se copia a NXPilot). Necesita el alcance Code para repositorios.")
            : Saved(_git.AzureDevOpsToken);
        GmailSecretState.Text = Saved(_mail.GmailClientSecret);
        WiAdoPatState.Text = Saved(_wi.DevOpsPat);
        WiGhTokenState.Text = Saved(_wi.GitHubToken);
        RefreshWorkItemOrigins(_wi);
        RefreshTeams();

        Dot(DatabricksDot, _db.ToConnection().IsComplete);
        Dot(GitDot, _git.GitHubToken.Length > 0 || _git.AzureDevOpsToken.Length > 0);
        var outlook = Session(MailProvider.Outlook);
        var gmail = Session(MailProvider.Gmail);
        OutlookSession.Text = outlook.Text;
        GmailSession.Text = gmail.Text;
        Dot(MailDot, outlook.SignedIn || gmail.SignedIn);

        var pbi = Services.PowerBiDesktop.Locate();
        PowerBiLocation.Text = pbi != null
            ? T($"Power BI Desktop encontrado: {pbi}", $"Power BI Desktop found: {pbi}", $"Power BI Desktop encontrado: {pbi}")
            : T("Power BI Desktop não encontrado. Instale pela Microsoft Store ou em powerbi.microsoft.com/desktop.", "Power BI Desktop not found. Install it from the Microsoft Store or powerbi.microsoft.com/desktop.", "Power BI Desktop no encontrado. Instálelo desde Microsoft Store o en powerbi.microsoft.com/desktop.");
        Dot(PowerBiDot, pbi != null);
        Dot(WindowsDot, _windows.AllowedProcesses.Count > 0);
    }

    private static (bool SignedIn, string Text) Session(MailProvider provider)
    {
        var token = new DpapiTokenStore(provider).LoadAsync().GetAwaiter().GetResult();
        return token == null
            ? (false, T("Não conectado.", "Not signed in.", "Sin sesión iniciada."))
            : (true, T($"Conectado · renova sozinho{(token.RefreshToken == null ? $" (expira {token.ExpiresAt.LocalDateTime:dd/MM HH:mm})" : "")}",
                       $"Signed in · renews automatically{(token.RefreshToken == null ? $" (expires {token.ExpiresAt.LocalDateTime:MM/dd HH:mm})" : "")}",
                       $"Sesión iniciada · se renueva automáticamente{(token.RefreshToken == null ? $" (vence {token.ExpiresAt.LocalDateTime:dd/MM HH:mm})" : "")}"));
    }

    private static void Dot(Ellipse dot, bool on)
    {
        dot.Fill = on ? Brushes.SeaGreen : Brushes.Silver;
        dot.ToolTip = on ? T("Configurada", "Configured", "Configurado") : T("Não configurada", "Not configured", "No configurado");
    }

    // ------------------------------------------------------------ o que está na tela

    private DatabricksSettings CollectDatabricks() => new()
    {
        Host = DbHostBox.Text.Trim(),
        WarehouseId = DbWarehouseBox.Text.Trim(),
        Catalog = DbCatalogBox.Text.Trim(),
        Schema = DbSchemaBox.Text.Trim(),
        Token = DbTokenBox.Password.Length > 0 ? DbTokenBox.Password : _db.Token,
    };

    private GitRepoSettings CollectGit() => new()
    {
        AzureDevOpsOrganizationUrl = AdoOrgBox.Text.Trim(),
        GitHubToken = GhTokenBox.Password.Length > 0 ? GhTokenBox.Password : _git.GitHubToken,
        AzureDevOpsToken = AdoPatBox.Password.Length > 0 ? AdoPatBox.Password : _git.AzureDevOpsToken,
        AzureDevOpsFromNXProject = AdoPatBox.Password.Length == 0 && _git.AzureDevOpsFromNXProject,
    };

    private MailSettings CollectMail() => new()
    {
        OutlookClientId = OutlookClientBox.Text.Trim(),
        OutlookTenant = OutlookTenantBox.Text.Trim() is { Length: > 0 } t ? t : "common",
        GmailClientId = GmailClientBox.Text.Trim(),
        GmailClientSecret = GmailSecretBox.Password.Length > 0 ? GmailSecretBox.Password : _mail.GmailClientSecret,
    };

    /// <summary>
    /// Conexões desta tela; as escolhas da tela de Work items (origem, projeto, raiz, "Mostrar")
    /// vêm do arquivo NA HORA de salvar — a tela de Work items pode tê-las mudado depois que esta abriu.
    /// </summary>
    private WorkItemSettings CollectWorkItems()
    {
        var latest = WorkItemSettings.Load();
        var s = CollectWorkItemConnections();
        s.LastSource = latest.LastSource;
        s.LastDevOpsProject = latest.LastDevOpsProject;
        s.LastGitHubProjectNumber = latest.LastGitHubProjectNumber;
        s.LastDevOpsRootId = latest.LastDevOpsRootId;
        s.LastGitHubRootIssue = latest.LastGitHubRootIssue;
        s.LastDevOpsScope = latest.LastDevOpsScope;
        return s;
    }

    private WorkItemSettings CollectWorkItemConnections() => new()
    {
        DevOpsOrganizationUrl = WiAdoOrgBox.Text.Trim(),
        DevOpsProject = WiAdoProjectBox.Text.Trim(),
        DevOpsPat = WiAdoPatBox.Password.Length > 0 ? WiAdoPatBox.Password : _wi.DevOpsPat,
        GitHubOwner = WiGhOwnerBox.Text.Trim(),
        GitHubOwnerIsOrganization = WiGhOrgCheck.IsChecked == true,
        GitHubProjectNumber = int.TryParse(WiGhNumberBox.Text.Trim(), out var n) && n > 0 ? n : 0,
        GitHubToken = WiGhTokenBox.Password.Length > 0 ? WiGhTokenBox.Password : _wi.GitHubToken,
        LastSource = _wi.LastSource,
        LastDevOpsProject = _wi.LastDevOpsProject,
        LastGitHubProjectNumber = _wi.LastGitHubProjectNumber,
        LastDevOpsRootId = _wi.LastDevOpsRootId,
        LastGitHubRootIssue = _wi.LastGitHubRootIssue,
    };

    // ------------------------------------------------------------ Work items

    /// <summary>Diz, para cada origem, qual conexão vale: a desta extensão, a do NXProject ou nenhuma.</summary>
    private void RefreshWorkItemOrigins(WorkItemSettings s)
    {
        var ado = s.DevOps();
        WiAdoOrigin.Text = ado switch
        {
            { Origin: WorkItemConnectionOrigin.Own } a => T($"✔ Usando esta configuração: {a.Connection.Project}", $"✔ Using this configuration: {a.Connection.Project}", $"✔ Usando esta configuración: {a.Connection.Project}"),
            { } a => T($"✔ Usando a conexão do NXProject: {a.Connection.OrganizationUrl} · {a.Connection.Project}. Preencha os três campos para usar outra.",
                       $"✔ Using the NXProject connection: {a.Connection.OrganizationUrl} · {a.Connection.Project}. Fill in all three fields to use another one.",
                       $"✔ Usando la conexión de NXProject: {a.Connection.OrganizationUrl} · {a.Connection.Project}. Complete los tres campos para usar otra."),
            null => T("Não configurado (e o NXProject não tem conexão do Azure DevOps neste usuário).", "Not configured (and NXProject has no Azure DevOps connection for this user).", "No configurado (y NXProject no tiene conexión de Azure DevOps en este usuario)."),
        };
        var gh = s.GitHub();
        WiGhOrigin.Text = gh switch
        {
            { Origin: WorkItemConnectionOrigin.Own } g => T($"✔ Usando esta configuração: {g.Connection.Owner} · projeto {g.Connection.Number}", $"✔ Using this configuration: {g.Connection.Owner} · project {g.Connection.Number}", $"✔ Usando esta configuración: {g.Connection.Owner} · proyecto {g.Connection.Number}"),
            { } g => T($"✔ Usando a conexão do NXProject: {g.Connection.Owner} · projeto {g.Connection.Number}. Preencha dono, número e token para usar outro.",
                       $"✔ Using the NXProject connection: {g.Connection.Owner} · project {g.Connection.Number}. Fill in owner, number and token to use another one.",
                       $"✔ Usando la conexión de NXProject: {g.Connection.Owner} · proyecto {g.Connection.Number}. Complete propietario, número y token para usar otro."),
            null => T("Não configurado (e o NXProject não tem GitHub Projects neste usuário).", "Not configured (and NXProject has no GitHub Projects for this user).", "No configurado (y NXProject no tiene GitHub Projects en este usuario)."),
        };
        WiAdoOrigin.Foreground = ado == null ? Brushes.DimGray : Brushes.SeaGreen;
        WiGhOrigin.Foreground = gh == null ? Brushes.DimGray : Brushes.SeaGreen;
        Dot(WorkItemsDot, ado != null || gh != null);
    }

    private async void OnWiDevOpsTestClick(object sender, RoutedEventArgs e) => await Run(WiAdoTestButton, WiStatus, async () =>
    {
        var s = CollectWorkItems();
        RefreshWorkItemOrigins(s);
        if (s.DevOps() is not { } ado) return (T("Preencha organização, projeto e PAT (ou configure no NXProject).", "Fill in organization, project and PAT (or set it up in NXProject).", "Complete organización, proyecto y PAT (o configúrelo en NXProject)."), Brushes.Firebrick);
        using var client = new AzureDevOpsClient(ado.Connection);
        var items = await client.MyOpenWorkItemsAsync(top: 20);
        return (T($"Azure DevOps OK · {ado.Connection.Project}: {items.Count} item(ns) aberto(s) atribuído(s) a você.", $"Azure DevOps OK · {ado.Connection.Project}: {items.Count} open item(s) assigned to you.", $"Azure DevOps OK · {ado.Connection.Project}: {items.Count} elemento(s) abierto(s) asignado(s) a usted."), Brushes.SeaGreen);
    });

    private async void OnWiGitHubTestClick(object sender, RoutedEventArgs e) => await Run(WiGhTestButton, WiStatus, async () =>
    {
        var s = CollectWorkItems();
        RefreshWorkItemOrigins(s);
        if (s.GitHub() is not { } gh) return (T("Preencha dono, número do projeto e token (ou configure no NXProject).", "Fill in owner, project number and token (or set it up in NXProject).", "Complete propietario, número de proyecto y token (o configúrelo en NXProject)."), Brushes.Firebrick);
        using var client = new GitHubProjectsClient(gh.Connection);
        var items = await client.MyOpenItemsAsync(maxPages: 2);
        return (T($"GitHub Projects OK · {client.ProjectTitle}: {items.Count} item(ns) aberto(s) atribuído(s) a você.", $"GitHub Projects OK · {client.ProjectTitle}: {items.Count} open item(s) assigned to you.", $"GitHub Projects OK · {client.ProjectTitle}: {items.Count} elemento(s) abierto(s) asignado(s) a usted."), Brushes.SeaGreen);
    });

    /// <summary>Apaga a configuração própria do DevOps: volta a valer a do NXProject.</summary>
    private void OnWiDevOpsClearClick(object sender, RoutedEventArgs e)
    {
        WiAdoOrgBox.Text = WiAdoProjectBox.Text = "";
        WiAdoPatBox.Password = "";
        _wi.DevOpsPat = "";
        WiAdoPatState.Text = "";
        RefreshWorkItemOrigins(CollectWorkItems());
    }

    /// <summary>Apaga a configuração própria do GitHub Projects: volta a valer a do NXProject.</summary>
    private void OnWiGitHubClearClick(object sender, RoutedEventArgs e)
    {
        WiGhOwnerBox.Text = WiGhNumberBox.Text = "";
        WiGhOrgCheck.IsChecked = false;
        WiGhTokenBox.Password = "";
        _wi.GitHubToken = "";
        WiGhTokenState.Text = "";
        RefreshWorkItemOrigins(CollectWorkItems());
    }

    // ------------------------------------------------------------ Teams

    private TeamsSettings CollectTeams() => new()
    {
        WebhookUrl = TeamsUrlBox.Password.Trim().Length > 0 ? TeamsUrlBox.Password.Trim() : _teams.WebhookUrl,
        SendDecisions = TeamsDecisionsCheck.IsChecked == true,
        SendTasks = TeamsTasksCheck.IsChecked == true,
        SendFailures = TeamsFailuresCheck.IsChecked == true,
        SendMessages = TeamsMessagesCheck.IsChecked == true,
        Subjects = TeamsSomeSubjects.IsChecked == true
            ? TeamsSubjectsPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList()
            : new(),
    };

    /// <summary>"Todos os assuntos" desabilita a lista; "Só os marcados" libera.</summary>
    private void OnTeamsSubjectsModeChanged(object sender, RoutedEventArgs e)
    {
        if (TeamsSubjectsPanel != null) TeamsSubjectsPanel.IsEnabled = TeamsSomeSubjects.IsChecked == true;
    }

    private void OnTeamsUrlChanged(object sender, RoutedEventArgs e) => RefreshTeams();

    /// <summary>Estado da URL (salva/cifrada, formato detectado) e o último erro de envio.</summary>
    private void RefreshTeams()
    {
        var url = TeamsUrlBox.Password.Trim().Length > 0 ? TeamsUrlBox.Password.Trim() : _teams.WebhookUrl;
        var saved = TeamsUrlBox.Password.Length == 0 && _teams.WebhookUrl.Length > 0
            ? T("Salva (cifrada). Deixe em branco para manter. ", "Saved (encrypted). Leave blank to keep it. ", "Guardada (cifrada). Déjela en blanco para mantenerla. ") : "";
        TeamsUrlState.Text = url.Length == 0 ? ""
            : !TeamsWebhookClient.IsValidUrl(url) ? T("A URL precisa começar com https://.", "The URL must start with https://.", "La URL debe comenzar con https://.")
            : saved + (TeamsWebhookClient.KindOf(url) == TeamsWebhookKind.IncomingWebhook
                ? T("Formato: Incoming Webhook (conector antigo do Office 365).", "Format: Incoming Webhook (legacy Office 365 connector).", "Formato: Incoming Webhook (conector antiguo de Office 365).")
                : T("Formato: Workflows (Adaptive Card).", "Format: Workflows (Adaptive Card).", "Formato: Workflows (Adaptive Card)."));
        if (Services.TeamsNotifier.LastError is { } error && TeamsStatus.Text.Length == 0)
            Status(TeamsStatus, T("Último envio falhou: ", "Last send failed: ", "El último envío falló: ") + error, Brushes.Firebrick);
        Dot(TeamsDot, url.Length > 0 && TeamsWebhookClient.IsValidUrl(url));
    }

    /// <summary>Manda um cartão de teste com o mesmo formato dos avisos (usa o que está na tela, sem salvar).</summary>
    private async void OnTeamsTestClick(object sender, RoutedEventArgs e) => await Run(TeamsTestButton, TeamsStatus, async () =>
    {
        var s = CollectTeams();
        if (!s.IsConfigured) return (T("Cole a URL do webhook do canal.", "Paste the channel webhook URL.", "Pegue la URL del webhook del canal."), Brushes.Firebrick);
        using var client = new TeamsWebhookClient(s.WebhookUrl);
        await client.SendAsync(Services.TeamsNotifier.MessageFor(ViewModels.NotificationKind.Task,
            T("Teste da extensão Teams", "Teams extension test", "Prueba de la extensión Teams"),
            T("Se este cartão apareceu no canal, o NXPilot já pode avisar aqui.", "If this card showed up in the channel, NXPilot can post here.", "Si esta tarjeta apareció en el canal, NXPilot ya puede avisar aquí.")));
        return (T("Enviado. Confira o canal no Teams.", "Sent. Check the channel in Teams.", "Enviado. Revise el canal en Teams."), Brushes.SeaGreen);
    });

    /// <summary>Esquece a URL (o canal deixa de receber avisos ao salvar).</summary>
    private void OnTeamsClearClick(object sender, RoutedEventArgs e)
    {
        TeamsUrlBox.Password = "";
        _teams.WebhookUrl = "";
        TeamsStatus.Text = "";
        RefreshTeams();
    }

    // ------------------------------------------------------------ Databricks

    private async void OnDatabricksTestClick(object sender, RoutedEventArgs e) => await Run(DbTestButton, DbStatus, async () =>
    {
        var conn = CollectDatabricks().ToConnection();
        if (!conn.IsComplete) return (T("Preencha host, warehouse e token.", "Fill in host, warehouse and token.", "Complete host, warehouse y token."), Brushes.Firebrick);
        Status(DbStatus, T("Conectando… (warehouse parado pode levar alguns minutos para ligar)", "Connecting… (a stopped warehouse may take a few minutes to start)", "Conectando… (un warehouse detenido puede tardar unos minutos en iniciar)"), Brushes.DimGray);
        using var svc = new DatabricksSqlService(conn);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.TestConnectionAsync();
        var catalogs = await svc.ListCatalogsAsync();
        return (T($"Conectado em {sw.Elapsed.TotalSeconds:F1}s. Catálogos: {string.Join(", ", catalogs.Take(8))}{(catalogs.Count > 8 ? "…" : "")}",
                  $"Connected in {sw.Elapsed.TotalSeconds:F1}s. Catalogs: {string.Join(", ", catalogs.Take(8))}{(catalogs.Count > 8 ? "…" : "")}",
                  $"Conectado en {sw.Elapsed.TotalSeconds:F1}s. Catálogos: {string.Join(", ", catalogs.Take(8))}{(catalogs.Count > 8 ? "…" : "")}"), Brushes.Green);
    });

    // ------------------------------------------------------------ Git

    private async void OnGitHubTestClick(object sender, RoutedEventArgs e) => await TestGit(GhTestButton, GitProvider.GitHub);
    private async void OnDevOpsTestClick(object sender, RoutedEventArgs e) => await TestGit(AdoTestButton, GitProvider.AzureDevOps);

    private Task TestGit(Button button, GitProvider provider) => Run(button, GitStatus, async () =>
    {
        var s = CollectGit();
        if (s.For(provider) == null) return (T($"Informe o token do {provider}.", $"Enter the {provider} token.", $"Ingrese el token de {provider}."), Brushes.Firebrick);
        var repos = await new GitRepoService(s).ListRepositoriesAsync(provider, s.AzureDevOpsOrganizationUrl);
        var names = string.Join(", ", repos.Take(6).Select(r => r.ToString()));
        return (T($"{provider}: {repos.Count} repositório(s). {names}{(repos.Count > 6 ? "…" : "")}",
                  $"{provider}: {repos.Count} repositor{(repos.Count == 1 ? "y" : "ies")}. {names}{(repos.Count > 6 ? "…" : "")}",
                  $"{provider}: {repos.Count} repositorio(s). {names}{(repos.Count > 6 ? "…" : "")}"), Brushes.Green);
    });

    // ------------------------------------------------------------ E-mail

    private async void OnOutlookLoginClick(object sender, RoutedEventArgs e) => await Login(OutlookLoginButton, MailProvider.Outlook);
    private async void OnGmailLoginClick(object sender, RoutedEventArgs e) => await Login(GmailLoginButton, MailProvider.Gmail);
    private void OnOutlookSignOutClick(object sender, RoutedEventArgs e) { new DpapiTokenStore(MailProvider.Outlook).Delete(); Refresh(); }
    private void OnGmailSignOutClick(object sender, RoutedEventArgs e) { new DpapiTokenStore(MailProvider.Gmail).Delete(); Refresh(); }
    private async void OnOutlookTestClick(object sender, RoutedEventArgs e) => await TestMail(OutlookTestButton, MailProvider.Outlook);
    private async void OnGmailTestClick(object sender, RoutedEventArgs e) => await TestMail(GmailTestButton, MailProvider.Gmail);

    private Task Login(Button button, MailProvider provider) => RunMail(button, async () =>
    {
        var tokens = CollectMail().Tokens(provider);
        if (tokens == null)
            return (provider == MailProvider.Outlook
                ? T("Informe o Client ID do app no Entra ID.", "Enter the Entra ID app Client ID.", "Ingrese el Client ID de la app de Entra ID.")
                : T("Informe o Client ID e o Client secret do Google Cloud.", "Enter the Google Cloud Client ID and Client secret.", "Ingrese el Client ID y el Client secret de Google Cloud."), Brushes.Firebrick);
        Status(MailStatus, T("Conclua o login no navegador que foi aberto…", "Finish signing in in the browser that just opened…", "Termine de iniciar sesión en el navegador que se acaba de abrir…"), Brushes.DimGray);
        await tokens.LoginAsync();
        Refresh();
        return (T($"{provider}: login concluído (somente leitura).", $"{provider}: signed in (read-only).", $"{provider}: sesión iniciada (solo lectura)."), Brushes.Green);
    });

    private Task TestMail(Button button, MailProvider provider) => RunMail(button, async () =>
    {
        var tokens = CollectMail().Tokens(provider);
        if (tokens == null) return (T("Configure e entre primeiro.", "Configure and sign in first.", "Configure e inicie sesión primero."), Brushes.Firebrick);
        using var mail = MailService.Create(provider, tokens);
        var list = await mail.ListInboxAsync(new MailQuery { Max = 5 });
        var lines = list.Select(m => $"{(m.IsRead ? "  " : "• ")}{m.ReceivedAt.LocalDateTime:dd/MM HH:mm}  {m.From}  —  {m.Subject}");
        return ($"{provider}: {list.Count}\n{string.Join('\n', lines)}", Brushes.Black);
    });

    // ------------------------------------------------------------ Power BI Desktop

    private async void OnPowerBiTestClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Power BI Project (*.pbip)|*.pbip", Title = T("Projeto Power BI", "Power BI project", "Proyecto Power BI") };
        if (dialog.ShowDialog(this) != true) return;
        PowerBiTestButton.IsEnabled = false;
        Status(PowerBiStatus, T("Abrindo no Power BI Desktop (pode levar alguns minutos)…", "Opening in Power BI Desktop (may take a few minutes)…", "Abriendo en Power BI Desktop (puede tardar unos minutos)…"), Brushes.DimGray);
        try
        {
            var png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nxpilot-powerbi-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await Services.PowerBiDesktop.OpenAndCaptureAsync(dialog.FileName, png);
            Status(PowerBiStatus, T($"Capturado: {png}", $"Captured: {png}", $"Capturado: {png}"), Brushes.Green);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(png) { UseShellExecute = true });
        }
        catch (Exception ex) { Status(PowerBiStatus, ex.Message, Brushes.Firebrick); }
        finally { PowerBiTestButton.IsEnabled = true; }
    }

    // ------------------------------------------------------------ Janelas do Windows

    private void OnReadAppClick(object sender, RoutedEventArgs e) => new ReadAppWindow { Owner = this }.Show();

    private void OnProcessesClick(object sender, RoutedEventArgs e) => new ProcessesWindow { Owner = this }.Show();

    private Services.WindowsUiSettings CollectWindows() => new()
    {
        AllowedProcesses = NXPilot.WindowsUi.WindowsUiPolicy.Parse(AllowedAppsBox.Text).Allowed.ToList(),
    };

    /// <summary>Lista TODAS as janelas (só aqui, na tela, para escolher o que liberar); o agente só vê as liberadas.</summary>
    private void OnListWindowsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var ui = new NXPilot.WindowsUi.WindowsUiService(CollectWindows().Policy);
            WindowsList.Items.Clear();
            foreach (var w in ui.ListWindows(allowedOnly: false).OrderByDescending(w => w.Allowed).ThenBy(w => w.ProcessName))
                WindowsList.Items.Add(new ListBoxItem
                {
                    Content = $"{(w.Allowed ? "✓" : "  ")} {w.ProcessName} — {w.Title}",
                    Tag = w,
                    Foreground = w.Allowed ? Brushes.Black : Brushes.Gray,
                    ToolTip = w.Allowed ? null : T($"Não liberado: adicione \"{w.ProcessName}\" à lista para o NXPilot poder ver.", $"Not allowed: add \"{w.ProcessName}\" to the list so NXPilot can see it.", $"No permitido: agregue \"{w.ProcessName}\" a la lista para que NXPilot pueda verlo."),
                });
            Status(WindowsStatus, T($"{WindowsList.Items.Count} janela(s). ✓ = liberada.", $"{WindowsList.Items.Count} window(s). ✓ = allowed.", $"{WindowsList.Items.Count} ventana(s). ✓ = permitida."), Brushes.DimGray);
        }
        catch (Exception ex) { Status(WindowsStatus, ex.Message, Brushes.Firebrick); }
    }

    private NXPilot.WindowsUi.WindowInfo? SelectedWindow => (WindowsList.SelectedItem as ListBoxItem)?.Tag as NXPilot.WindowsUi.WindowInfo;

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e) =>
        WindowCaptureButton.IsEnabled = WindowReadButton.IsEnabled = SelectedWindow is { Allowed: true };

    private void OnWindowCaptureClick(object sender, RoutedEventArgs e) => WithWindow((ui, w) =>
    {
        var png = ui.Capture(w, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nxpilot-janela-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(png) { UseShellExecute = true });
        return T($"Capturado: {png}", $"Captured: {png}", $"Capturado: {png}");
    });

    private void OnWindowReadClick(object sender, RoutedEventArgs e) => WithWindow((ui, w) =>
    {
        var elements = ui.ReadElements(w);
        return T($"{elements.Count} elemento(s) — é isto que o agente receberia:\n\n", $"{elements.Count} element(s) — this is what the agent would get:\n\n", $"{elements.Count} elemento(s) — esto es lo que recibiría el agente:\n\n") + ui.ToAgentContext(w, elements);
    });

    private void WithWindow(Func<NXPilot.WindowsUi.WindowsUiService, NXPilot.WindowsUi.WindowInfo, string> action)
    {
        if (SelectedWindow is not { } w) return;
        try
        {
            using var ui = new NXPilot.WindowsUi.WindowsUiService(CollectWindows().Policy); // confere a lista como está na tela
            Status(WindowsStatus, action(ui, w), Brushes.Black);
        }
        catch (Exception ex) { Status(WindowsStatus, ex.Message, Brushes.Firebrick); }
    }

    // ------------------------------------------------------------ salvar

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CollectDatabricks().Save();
            CollectGit().Save();
            CollectMail().Save();
            CollectWorkItems().Save();
            CollectTeams().Save();
            CollectWindows().Save();
            DialogResult = true;
        }
        catch (Exception ex) { Status(SaveStatus, T("Não salvou: ", "Not saved: ", "No se guardó: ") + ex.Message, Brushes.Firebrick); }
    }

    // ------------------------------------------------------------ apoio

    private async Task Run(Button button, TextBlock status, Func<Task<(string Text, Brush Color)>> action)
    {
        button.IsEnabled = false;
        try { var (text, color) = await action(); Status(status, text, color); }
        catch (Exception ex) { Status(status, ex.Message, Brushes.Firebrick); }
        finally { button.IsEnabled = true; }
    }

    private async Task RunMail(Button button, Func<Task<(string Text, Brush Color)>> action)
    {
        button.IsEnabled = false;
        try { var (text, color) = await action(); Status(MailStatus, text, color); }
        catch (Exception ex) { Status(MailStatus, ex.Message, Brushes.Firebrick); }
        finally { button.IsEnabled = true; }
    }

    private static void Status(TextBlock target, string text, Brush color) { target.Text = text; target.Foreground = color; }
    private static void Status(TextBox target, string text, Brush color) { target.Text = text; target.Foreground = color; }
}
