// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NXPilot.Agents;
using NXPilot.Desktop.Services;
using NXPilot.Desktop.ViewModels;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Assistente de Nova tarefa: só coleta e valida; quem roda os agentes é o MainViewModel.
/// Lembra repositório, modelo e revisor da última tarefa (settings.json).
/// </summary>
public partial class NewTaskWindow : Window
{
    public NewTaskInfo? Result { get; private set; }

    /// <summary>Pré-preenchida a partir de um work item do Azure DevOps ou do GitHub Projects (título, descrição, critérios).</summary>
    public NewTaskWindow(NXPilot.Integrations.WorkItem item, NXPilot.Integrations.WorkItemLink? link = null)
        : this([(item, link?.Project)]) { }

    /// <summary>
    /// A partir de um ou mais work items (de qualquer nível): cada um vira citação, como um anexo
    /// ([story01], [task01]…). Com um só, título, descrição e critérios vêm dele; com vários, a
    /// intenção começa com os marcadores para você escrever o que fazer com eles.
    /// </summary>
    public NewTaskWindow(IReadOnlyList<(NXPilot.Integrations.WorkItem Item, string? Project)> items) : this()
    {
        var markers = new List<string>();
        foreach (var (w, project) in items)
        {
            if (w.Id <= 0) continue; // rascunho do GitHub: sem número, sem citação
            var c = NXPilot.Planning.TaskCitations.ForWorkItem(_citations, w.Type, w.Id, w.Title, project, w.Url);
            _citations.Add(c);
            markers.Add(c.Marker);
        }
        RefreshCitations();
        if (items.Count == 1)
        {
            var w = items[0].Item;
            TitleBox.Text = w.Id > 0 ? $"#{w.Id} · {w.Title}" : w.Title; // rascunho do GitHub não tem número
            IntentBox.Text = (w.Description.Length > 0 ? w.Description : w.Title) + (markers.Count > 0 ? "\n\n" + markers[0] : "");
            CriteriaBox.Text = w.AcceptanceCriteria;
        }
        else
        {
            TitleBox.Text = string.Join(" · ", items.Take(3).Select(i => $"#{i.Item.Id}")) + (items.Count > 3 ? $" (+{items.Count - 3})" : "");
            IntentBox.Text = string.Join(", ", markers) + ": ";
            Loaded += (_, _) => { IntentBox.Focus(); IntentBox.CaretIndex = IntentBox.Text.Length; };
        }
        Title = T($"Nova tarefa · {items.Count} work item(s) citado(s)", $"New task · {items.Count} cited work item(s)", $"Nueva tarea · {items.Count} work item(s) citado(s)");
    }

    /// <summary>Work item vinculado (vai para o prompt e para o Histórico); nulo = tarefa sem vínculo.</summary>
    private NXPilot.Integrations.WorkItemLink? _workItem;

    private void SetWorkItem(NXPilot.Integrations.WorkItemLink? link)
    {
        _workItem = link;
        if (link != null) Title = T($"Nova tarefa · vinculada a {link.Label}", $"New task · linked to {link.Label}", $"Nueva tarea · vinculada a {link.Label}");
    }

    /// <summary>
    /// Clonar do Histórico: mesmos título, intenção, critérios, agente, modelos, revisor e pasta
    /// (repositório ou assunto). Tudo continua editável; o Criar valida como numa tarefa nova.
    /// </summary>
    public NewTaskWindow(NewTaskInfo clone) : this()
    {
        TitleBox.Text = clone.Title;
        var (intent, citations) = NXPilot.Planning.TaskCitations.SplitLegend(clone.Intent);
        IntentBox.Text = intent;
        foreach (var c in citations) _citations.Add(c); // a legenda volta como lista
        RefreshCitations();
        CriteriaBox.Text = clone.Criteria is "(não informados)" or "(not provided)" or "(no informado)" ? "" : clone.Criteria;
        Select(VersioningBox, clone.Versioning.ToString());
        if (clone.Versioning == VersioningMode.Git) RepoBox.Text = clone.RepoPath;
        else LoadSubjects(clone.Subject);
        Select(TaskModeBox, clone.AsTemplate ? "Hook" : "Online"); // clonar um hook cria outro hook
        Select(AgentBox, clone.Executor.ToString());
        Select(ReviewerBox, clone.Reviewer.ToString());
        AutoFixBox.IsChecked = clone.AutoFixFindings;
        Select(ModelBox, clone.Model ?? "");
        if (clone.CodexModel is { Length: > 0 } codexModel) CodexModelBox.Text = codexModel;
        Title = T("Nova tarefa (clonada do Histórico)", "New task (cloned from History)", "Nueva tarea (clonada del Historial)");
        _workItem = clone.WorkItem; // o vínculo com o work item vem junto
    }

    /// <summary>
    /// Editar a tarefa que falhou antes de tentar de novo: tudo editável (texto, agente, modelos,
    /// revisor), menos o lugar e o modo — o worktree já é daquele repositório/assunto.
    /// </summary>
    public NewTaskWindow(NewTaskInfo current, bool editing) : this(current)
    {
        if (!editing) return;
        _editing = true;
        foreach (var c in new Control[] { TaskModeBox, VersioningBox, RepoBox, SubjectBox, SummarySubjectBox }) c.IsEnabled = false;
        CreateButton.SetResourceReference(ContentProperty, "Task_SalvarETentar");
        Title = T("Editar tarefa e tentar novamente", "Edit task and try again", "Editar tarea e intentar de nuevo");
    }

    public NewTaskWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        var s = AppSettings.Load();
        RepoBox.Text = s.LastRepo ?? "";
        // Antes da consulta à conta (e se ela falhar), o modelo testado já está na lista e escolhido.
        CodexModelBox.ItemsSource = NXPilot.Agents.CodexAppServerAdapter.TestedModels.ToList();
        CodexModelBox.Text = s.CodexModel is { Length: > 0 } saved ? saved : NXPilot.Agents.CodexAppServerAdapter.TestedModels[0];
        Select(ModelBox, s.LastModel ?? "");
        Select(ReviewerBox, s.Reviewer ?? nameof(ReviewerKind.Codex));
        Select(VersioningBox, s.Versioning ?? nameof(VersioningMode.Local));
        UpdateVersioningHint();
        LoadSubjects(s.LastSubject);

        // Quem tem só o Codex (ou só o Claude) também cria tarefa: mostra o que está instalado e
        // já seleciona o último usado ou, na primeira vez, o que existe na máquina.
        _claudeInstalled = ReviewerChoice.IsInstalled(AgentKind.Claude);
        _codexInstalled = ReviewerChoice.IsInstalled(AgentKind.Codex);
        var notInstalled = T(" — não instalado", " — not installed", " — no instalado");
        AgentClaudeItem.Content = "Claude Code" + (_claudeInstalled ? "" : notInstalled);
        AgentCodexItem.Content = "Codex" + (_codexInstalled ? "" : notInstalled);
        AgentNXAgenticItem.Content = "NXAgentic" + T(" — em construção", " — under construction", " — en construcción");
        Select(AgentBox, s.Executor ?? (!_claudeInstalled && _codexInstalled ? nameof(ExecutorKind.Codex) : nameof(ExecutorKind.Claude)));
        UpdateAgentHint();

        // Mais espaço para o texto: com a última configuração ainda válida, as opções começam
        // recolhidas (fica o resumo); na primeira vez, ou com algo a acertar, começam abertas.
        // O padrão do assunto vence a "última escolha" (aplicada logo acima).
        ApplySubjectDefaults();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        OnTaskModeChanged(this, null!);
        SetOptionsVisible(!ConfigLooksReady());
        UpdateCounters();
        UpdateSummary();

        Loaded += async (_, _) =>
        {
            await PreferCodexWithoutClaudeLoginAsync();
            (SelectedVersioning == VersioningMode.Git && RepoBox.Text.Length == 0 ? RepoBox : TitleBox).Focus();
            await LoadCodexModelsAsync();
            UpdateSummary();
        };
    }

    /// <summary>
    /// Lista do "Modelo do Codex" vinda da PRÓPRIA conta (model/list do Codex) — evita o erro
    /// de modelo não aceito pela conta ChatGPT. Marca os modelos já testados com o NXPilot.
    /// </summary>
    private async Task LoadCodexModelsAsync()
    {
        var tested = string.Join(", ", NXPilot.Agents.CodexAppServerAdapter.TestedModels);
        CodexModelHint.Text = T("Consultando os modelos da sua conta no Codex…", "Querying your account's Codex models…", "Consultando los modelos de Codex de su cuenta…");
        var models = await NXPilot.Agents.CodexAppServerAdapter.ListModelsAsync();
        var typed = CodexModelBox.Text.Trim();
        // Testados primeiro (sempre na lista, mesmo fora da resposta da conta); depois os da conta.
        CodexModelBox.ItemsSource = NXPilot.Agents.CodexAppServerAdapter.TestedModels
            .Concat(models.Select(m => m.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Vazio, ou um modelo que a conta não aceita (ex.: o padrão do config.toml): volta para o testado.
        if (typed.Length == 0 || (models.Count > 0 && !models.Any(m => string.Equals(m.Id, typed, StringComparison.OrdinalIgnoreCase))
                                  && !NXPilot.Agents.CodexAppServerAdapter.TestedModels.Contains(typed, StringComparer.OrdinalIgnoreCase)))
            CodexModelBox.Text = NXPilot.Agents.CodexAppServerAdapter.TestedModels[0];
        CodexModelHint.Text = models.Count == 0
            ? T($"Não deu para listar (Codex instalado e logado?). Testado com o NXPilot: {tested}.", $"Could not list models (is Codex installed and signed in?). Tested with NXPilot: {tested}.", $"No se pudieron listar los modelos (¿Codex está instalado y con sesión iniciada?). Probado con NXPilot: {tested}.")
            : T($"{models.Count} modelo(s) da sua conta · testado com o NXPilot: {string.Join(", ", models.Where(m => m.Tested).Select(m => m.Id + " ✓").DefaultIfEmpty(tested + " (não está na sua lista)"))}",
                $"{models.Count} model(s) on your account · tested with NXPilot: {string.Join(", ", models.Where(m => m.Tested).Select(m => m.Id + " ✓").DefaultIfEmpty(tested + " (not on your list)"))}",
                $"{models.Count} modelo(s) en su cuenta · probado con NXPilot: {string.Join(", ", models.Where(m => m.Tested).Select(m => m.Id + " ✓").DefaultIfEmpty(tested + " (no está en su lista)"))}");
    }

    private readonly bool _claudeInstalled, _codexInstalled;

    /// <summary>
    /// Claude Code sem login (precisa de assinatura paga) e Codex instalado (funciona com a conta
    /// ChatGPT, inclusive gratuita): a tarefa já vem com o Codex. Dá para escolher o Claude — ao
    /// rodar, o NXPilot abre o login dele no navegador.
    /// </summary>
    private async Task PreferCodexWithoutClaudeLoginAsync()
    {
        if (!_claudeInstalled || !_codexInstalled || NXPilot.Agents.ClaudeCodeAdapter.Locate() is not { } claude) return;
        bool? logged;
        try { logged = await NXPilot.Agents.AgentLogin.IsLoggedInAsync(claude: true, claude, CancellationToken.None); }
        catch { return; }
        if (logged != false) return;
        AgentClaudeItem.Content = "Claude Code" + T(" — sem login", " — not signed in", " — sin inicio de sesión");
        if (SelectedExecutor != ExecutorKind.Claude || _editing) return;
        Select(AgentBox, nameof(ExecutorKind.Codex));
        AgentHint.Text = T("O Claude Code está sem login (ele precisa de assinatura): a tarefa vai com o Codex, que funciona com a sua conta ChatGPT. Se preferir o Claude, escolha acima — o login abre no navegador ao rodar.",
                           "Claude Code is not signed in (it needs a subscription): the task goes with Codex, which works with your ChatGPT account. If you prefer Claude, pick it above — sign-in opens in the browser when it runs.",
                           "Claude Code no tiene sesión iniciada (necesita suscripción): la tarea va con Codex, que funciona con su cuenta ChatGPT. Si prefiere Claude, elíjalo arriba — el inicio de sesión se abre en el navegador al ejecutar.");
    }

    /// <summary>Editando uma tarefa: o agente escolhido nela não é trocado sozinho.</summary>
    private bool _editing;

    // ------------------------------------------------------------ texto: limites e contadores

    /// <summary>Limites aceitos (o MaxLength das caixas); item do DevOps/GitHub pode chegar maior e é barrado no Criar.</summary>
    public const int TitleMax = 150, IntentMax = 10_000, CriteriaMax = 5_000;

    private void OnTextChanged(object sender, TextChangedEventArgs e) => UpdateCounters();

    private void UpdateCounters()
    {
        if (TitleCount == null || IntentCount == null || CriteriaCount == null) return; // TextChanged durante o InitializeComponent
        Count(TitleCount, TitleBox.Text.Length, TitleMax);
        Count(IntentCount, IntentBox.Text.Length, IntentMax);
        Count(CriteriaCount, CriteriaBox.Text.Length, CriteriaMax);
    }

    /// <summary>"1.234 / 10.000 · faltam 8.766": laranja nos últimos 10%, vermelho se passou.</summary>
    private static void Count(TextBlock target, int length, int max)
    {
        var left = max - length;
        target.Text = left >= 0
            ? T($"{length:N0} / {max:N0} · faltam {left:N0}", $"{length:N0} / {max:N0} · {left:N0} left", $"{length:N0} / {max:N0} · faltan {left:N0}")
            : T($"{length:N0} / {max:N0} · passou {-left:N0}", $"{length:N0} / {max:N0} · {-left:N0} over", $"{length:N0} / {max:N0} · sobran {-left:N0}");
        target.Foreground = left < 0 ? Brushes.Firebrick : left < max / 10 ? Brushes.DarkOrange : (Brush)Application.Current.FindResource("SecondaryTextBrush");
    }

    // ------------------------------------------------------------ citar arquivos (arrastar ou 📎)

    /// <summary>Última caixa de texto com foco (Intenção ou Critérios): é onde o 📎 Citar arquivo insere.</summary>
    private TextBox? _lastTextBox;

    private void OnTextBoxFocus(object sender, RoutedEventArgs e) => _lastTextBox = sender as TextBox;

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // texto arrastado: comportamento normal da caixa
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>Arquivos/pastas soltos na caixa: o caminho entra no ponto onde foram soltos.</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (sender is not TextBox box || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var index = box.GetCharacterIndexFromPoint(e.GetPosition(box), snapToText: true);
        InsertReferences(box, paths, index < 0 ? box.Text.Length : index + 1);
    }

    private void OnCiteFileClick(object sender, RoutedEventArgs e)
    {
        var box = _lastTextBox ?? IntentBox;
        var dialog = new OpenFileDialog
        {
            Title = T("Citar arquivos na tarefa (só o caminho entra no texto)", "Cite files in the task (only the path goes into the text)", "Citar archivos en la tarea (solo la ruta entra en el texto)"),
            Multiselect = true,
        };
        var baseFolder = ReferenceBaseFolder();
        if (baseFolder != null && Directory.Exists(baseFolder)) dialog.InitialDirectory = baseFolder;
        if (dialog.ShowDialog(this) == true) InsertReferences(box, dialog.FileNames, box.CaretIndex);
    }

    private void OnCiteFolderClick(object sender, RoutedEventArgs e)
    {
        var box = _lastTextBox ?? IntentBox;
        var dialog = new OpenFolderDialog
        {
            Title = T("Citar pastas na tarefa (só o caminho entra no texto)", "Cite folders in the task (only the path goes into the text)", "Citar carpetas en la tarea (solo la ruta entra en el texto)"),
            Multiselect = true,
        };
        var baseFolder = ReferenceBaseFolder();
        if (baseFolder != null && Directory.Exists(baseFolder)) dialog.InitialDirectory = baseFolder;
        if (dialog.ShowDialog(this) == true) InsertReferences(box, dialog.FolderNames, box.CaretIndex);
    }

    /// <summary>Pasta de trabalho da tarefa: o assunto (local) ou o repositório (git).</summary>
    private string? ReferenceBaseFolder() =>
        SelectedVersioning == VersioningMode.Local ? SelectedSubject?.Path : RepoBox.Text.Trim() is { Length: > 0 } repo ? repo : null;

    /// <summary>
    /// Dentro do assunto/repositório: caminho relativo (`relatorios/q2.xlsx`) — o agente trabalha numa
    /// cópia (worktree) dessa pasta e acha o arquivo pelo mesmo caminho. Fora: o caminho completo.
    /// </summary>
    private string Reference(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var folder = Directory.Exists(full); // pasta: termina com "/" para o agente saber que é pasta
        if (ReferenceBaseFolder() is { } baseFolder)
        {
            var baseFull = Path.GetFullPath(baseFolder).TrimEnd('\\', '/');
            if (string.Equals(full, baseFull, StringComparison.OrdinalIgnoreCase)) return "./"; // o próprio assunto/repositório
            var root = baseFull + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..].Replace('\\', '/') + (folder ? "/" : "");
        }
        return full + (folder ? "\\" : "");
    }

    /// <summary>Citações da tarefa: no texto só o marcador ([xlsx01], [folder01]…); a lista fica abaixo da intenção e vira legenda para o agente.</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<NXPilot.Planning.Citation> _citations = new();

    /// <summary>Cita os caminhos no ponto <paramref name="index"/>: o mesmo arquivo reaproveita o número.</summary>
    private void InsertReferences(TextBox box, IEnumerable<string> paths, int index)
    {
        var markers = new List<string>();
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var citation = _citations.FirstOrDefault(c => string.Equals(c.FullPath, full, StringComparison.OrdinalIgnoreCase));
            if (citation == null)
            {
                var kind = NXPilot.Planning.TaskCitations.KindOf(full, Directory.Exists(full)); // [xlsx01], [docx01], [folder01]
                _citations.Add(citation = new NXPilot.Planning.Citation(kind, NXPilot.Planning.TaskCitations.NextNumber(_citations, kind), Reference(full), full));
            }
            markers.Add(citation.Marker);
        }
        RefreshCitations();
        var text = string.Join(", ", markers.Distinct());
        index = Math.Clamp(index, 0, box.Text.Length);
        // Espaço antes/depois quando cai colado numa palavra.
        if (index > 0 && !char.IsWhiteSpace(box.Text[index - 1])) text = " " + text;
        if (index < box.Text.Length && !char.IsWhiteSpace(box.Text[index])) text += " ";
        if (box.MaxLength > 0 && box.Text.Length + text.Length > box.MaxLength)
        {
            ErrorText.Text = T("Não coube no limite de caracteres desta caixa.", "It does not fit within this box's character limit.", "No cabe en el límite de caracteres de este cuadro.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        box.Text = box.Text.Insert(index, text);
        box.CaretIndex = index + text.Length;
        box.Focus();
    }

    /// <summary>✕ na lista: tira os marcadores dela da intenção e dos critérios e renumera as seguintes do mesmo tipo.</summary>
    private void OnRemoveCitationClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NXPilot.Planning.Citation removed) return;
        IntentBox.Text = NXPilot.Planning.TaskCitations.RemoveAndRenumber(IntentBox.Text, removed);
        CriteriaBox.Text = NXPilot.Planning.TaskCitations.RemoveAndRenumber(CriteriaBox.Text, removed);
        // Renumera só o mesmo tipo: tirando xlsx01, o xlsx02 vira xlsx01.
        var rest = _citations.Where(c => c != removed)
            .Select(c => c.Kind == removed.Kind && c.Number > removed.Number ? c with { Number = c.Number - 1 } : c).ToList();
        _citations.Clear();
        foreach (var c in rest) _citations.Add(c);
        RefreshCitations();
    }

    private void RefreshCitations()
    {
        CitationsList.ItemsSource = _citations;
        CitationsPanel.Visibility = _citations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ hook

    private bool IsHookMode => (TaskModeBox?.SelectedItem as ComboBoxItem)?.Tag as string == "Hook";

    /// <summary>Modo da tarefa: o + {texto} e a explicação só fazem sentido no Hook.</summary>
    private void OnTaskModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskModeHint == null || InsertPlaceholderButton == null) return; // durante o InitializeComponent
        InsertPlaceholderButton.Visibility = IsHookMode ? Visibility.Visible : Visibility.Collapsed;
        TaskModeHint.Text = IsHookMode
            ? T("O agente planeja, você aprova e faz UMA execução de teste com um {texto} de exemplo. Os riscos aprovados no teste ficam pré-aprovados; aí você arma o hook e cada evento cria uma tarefa comum com este plano. Só pede você se disparar a menos de 20 s do anterior, acima do limite por hora, ou se aparecer um risco ALTO que não estava no teste.",
                "The agent plans, you approve and do ONE test run with a sample {texto}. The risks approved in the test become pre-approved; then you arm the hook and each event creates a regular task with this plan. It only asks you if it fires less than 20 s after the previous one, above the hourly limit, or if a HIGH risk shows up that was not in the test.",
                "El agente planifica, usted aprueba y hace UNA ejecución de prueba con un {texto} de ejemplo. Los riesgos aprobados en la prueba quedan preaprobados; luego arma el hook y cada evento crea una tarea común con este plan. Solo le pregunta si se dispara a menos de 20 s del anterior, por encima del límite por hora, o si aparece un riesgo ALTO que no estaba en la prueba.")
            : T("O agente planeja e, depois da sua aprovação, executa agora.", "The agent plans and, after your approval, runs now.", "El agente planifica y, tras su aprobación, ejecuta ahora.");
        UpdateSummary();
    }

    /// <summary>Põe {texto} no cursor da intenção: ali entra o que o evento trouxer (como dado).</summary>
    private void OnInsertPlaceholderClick(object sender, RoutedEventArgs e)
    {
        var box = _lastTextBox ?? IntentBox;
        var index = Math.Clamp(box.CaretIndex, 0, box.Text.Length);
        box.Text = box.Text.Insert(index, NXPilot.Planning.TemplateTriggers.Placeholder);
        box.CaretIndex = index + NXPilot.Planning.TemplateTriggers.Placeholder.Length;
        box.Focus();
    }

    // ------------------------------------------------------------ opções: recolher e resumo

    private bool _optionsVisible = true;

    private void OnToggleOptionsClick(object sender, RoutedEventArgs e) => SetOptionsVisible(!_optionsVisible);

    /// <summary>Para a captura (--shot --window newtask --options): abre as opções.</summary>
    public void ShowOptions() => SetOptionsVisible(true);

    /// <summary>Para a captura (--cite caminho): cita como se o arquivo fosse arrastado para o fim da intenção.</summary>
    public void CiteForShot(string text, IEnumerable<string> paths)
    {
        IntentBox.Text = text;
        InsertReferences(IntentBox, paths, IntentBox.Text.Length);
    }

    /// <summary>Recolhidas, a coluna da direita some e o texto ganha a largura toda.</summary>
    private void SetOptionsVisible(bool visible)
    {
        _optionsVisible = visible;
        OptionsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OptionsColumn.Width = visible ? new GridLength(430) : new GridLength(0);
        GapColumn.Width = visible ? new GridLength(24) : new GridLength(0);
        OptionsToggle.Content = visible ? T("Ocultar opções ▸", "Hide options ▸", "Ocultar opciones ▸") : T("◂ Opções", "◂ Options", "◂ Opciones");
    }

    /// <summary>A configuração lembrada já serve? (pasta existe e o agente escolhido está instalado)</summary>
    private bool ConfigLooksReady()
    {
        var folderOk = SelectedVersioning == VersioningMode.Local ? SelectedSubject != null : Directory.Exists(Path.Combine(RepoBox.Text.Trim(), ".git")) || File.Exists(Path.Combine(RepoBox.Text.Trim(), ".git"));
        var agentOk = !NXAgenticSelected && (SelectedExecutor == ExecutorKind.Codex ? _codexInstalled && CodexModelBox.Text.Trim().Length > 0 : _claudeInstalled);
        return folderOk && agentOk;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    /// <summary>"📁 Local · testePilot01  ·  🤖 Claude Code (padrão da conta)  ·  🔍 revisor Codex (gpt-5.6-terra)"</summary>
    private void UpdateSummary()
    {
        if (SummaryText == null || ReviewerBox == null || ModelBox == null || CodexModelBox == null) return; // durante o InitializeComponent
        // Local: o assunto (com o agrupador, "NXPilot › testePilot01") é uma lista no próprio resumo.
        // Git: o repositório vai no texto.
        var local = SelectedVersioning == VersioningMode.Local;
        SummarySubjectPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        SummarySubjectLabel.Text = "📁 " + T("Local Assunto:", "Local Subject:", "Asunto local:");
        SummarySubjectBox.ToolTip = SelectedSubject?.Path;
        var folder = local ? null
            : "📁 Git · " + (RepoBox.Text.Trim() is { Length: > 0 } repo ? Path.GetFileName(repo.TrimEnd('\\', '/')) : T("(escolha o repositório)", "(choose the repository)", "(elija el repositorio)"));
        var codexModel = CodexModelBox.Text.Trim();
        var agent = NXAgenticSelected ? "NXAgentic"
            : SelectedExecutor == ExecutorKind.Codex ? "Codex" + (codexModel.Length > 0 ? $" ({codexModel})" : "")
            : "Claude Code" + ((ModelBox.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } m ? $" ({m})" : T(" (padrão da conta)", " (account default)", " (predeterminado de la cuenta)"));
        var reviewer = ((ReviewerBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "Claude" => "Claude",
            "None" => T("nenhum", "none", "ninguno"),
            _ => "Codex" + (codexModel.Length > 0 ? $" ({codexModel})" : ""),
        };
        // Rótulo antes de cada valor: "Agente: Claude Code (…) · Revisor: Codex (…)".
        // Sem revisor não há achados para corrigir.
        var hasReviewer = (ReviewerBox.SelectedItem as ComboBoxItem)?.Tag as string != "None";
        if (AutoFixBox != null) AutoFixBox.IsEnabled = hasReviewer;
        SummaryText.Text = (IsHookMode ? "⚡ Hook   ·   " : "")
            + (folder == null ? "" : folder + "   ·   ")
            + $"🤖 {T("Agente:", "Agent:", "Agente:")} {agent}   ·   🔍 {T("Revisor:", "Reviewer:", "Revisor:")} {reviewer}"
            + (hasReviewer && AutoFixBox?.IsChecked == true ? "   ·   🔧 " + T("corrige os achados sozinho", "fixes findings by itself", "corrige los hallazgos solo") : "");
        var fullPath = SelectedVersioning == VersioningMode.Local ? SelectedSubject?.Path : RepoBox.Text.Trim();
        SummaryText.ToolTip = SummaryText.Text + (string.IsNullOrEmpty(fullPath) ? "" : "\n" + fullPath);
    }

    /// <summary>Edição comercial (repositório privado): já aparece na lista, mas ainda não executa tarefas.</summary>
    private bool NXAgenticSelected => AgentBox.SelectedItem == AgentNXAgenticItem;

    private static string NXAgenticUnderConstruction => T(
        "O NXAgentic (edição comercial da Nexus XData) está em construção e ainda não executa tarefas. Por enquanto, escolha o Claude Code ou o Codex.",
        "NXAgentic (Nexus XData's commercial edition) is under construction and cannot run tasks yet. For now, choose Claude Code or Codex.",
        "NXAgentic (la edición comercial de Nexus XData) está en construcción y aún no ejecuta tareas. Por ahora, elija Claude Code o Codex.");

    private ExecutorKind SelectedExecutor =>
        Enum.TryParse<ExecutorKind>((AgentBox.SelectedItem as ComboBoxItem)?.Tag as string, out var kind) ? kind : ExecutorKind.Claude;

    private void OnAgentChanged(object sender, SelectionChangedEventArgs e) => UpdateAgentHint();

    /// <summary>Explica, embaixo do Agente, que o gate vale igual e quem vai revisar.</summary>
    private void UpdateAgentHint()
    {
        if (AgentHint == null) return; // SelectionChanged dispara durante o InitializeComponent
        UpdateSummary();
        if (NXAgenticSelected) { AgentHint.Text = NXAgenticUnderConstruction; return; }
        var codex = SelectedExecutor == ExecutorKind.Codex;
        ModelBox.IsEnabled = !codex; // o modelo do Claude só vale com o Claude executando (o do Codex fica abaixo)
        ModelBox.ToolTip = codex ? T("Modelo do Claude Code — com o Codex executando, vale o \"Modelo do Codex\" abaixo.", "Claude Code model — with Codex executing, the \"Codex model\" below applies.", "Modelo de Claude Code — con Codex ejecutando, vale el \"Modelo de Codex\" de abajo.") : null;
        var name = codex ? "Codex" : "Claude Code";
        var hint = T($"O {name} executa sob o mesmo gate: cada comando e arquivo passa pela cascata e pelas suas decisões.",
                     $"{name} runs under the same gate: every command and file goes through the cascade and your decisions.",
                     $"{name} se ejecuta bajo el mismo gate: cada comando y archivo pasa por la cascada y por sus decisiones.");
        if (_claudeInstalled != _codexInstalled)
            hint += " " + T($"Só o {(_codexInstalled ? "Codex" : "Claude Code")} está instalado: a revisão será dele mesmo, numa sessão nova e somente leitura.",
                            $"Only {(_codexInstalled ? "Codex" : "Claude Code")} is installed: it reviews itself, in a new read-only session.",
                            $"Solo {(_codexInstalled ? "Codex" : "Claude Code")} está instalado: se revisará a sí mismo, en una sesión nueva y de solo lectura.");
        AgentHint.Text = hint;
    }

    private VersioningMode SelectedVersioning =>
        Enum.TryParse<VersioningMode>((VersioningBox.SelectedItem as ComboBoxItem)?.Tag as string, out var mode) ? mode : VersioningMode.Local;

    private ShadowSubject? SelectedSubject => SubjectBox.SelectedItem as ShadowSubject;

    private void OnVersioningChanged(object sender, SelectionChangedEventArgs e) => UpdateVersioningHint();

    /// <summary>Local: um assunto (pastas sem git, histórico no repositório-sombra). Git: o repositório do projeto.</summary>
    private void UpdateVersioningHint()
    {
        if (VersioningHint == null || SubjectRow == null || RepoRow == null) return; // SelectionChanged dispara durante o InitializeComponent
        var local = SelectedVersioning == VersioningMode.Local;
        SubjectRow.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        RepoRow.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        VersioningHint.Text = local
            ? T("Cada assunto é uma pasta comum, sem precisar de git. O NXPilot guarda o histórico à parte (a pasta não ganha .git) e só grava nela quando você aplicar o resultado. Dá para mover o assunto para outra pasta ou para o GitHub depois.",
                "Each subject is a regular folder, no git needed. NXPilot keeps the history separately (no .git is added to the folder) and only writes to it when you apply the result. The subject can be moved to another folder or to GitHub later.",
                "Cada asunto es una carpeta común, sin necesidad de git. NXPilot guarda el historial aparte (la carpeta no recibe .git) y solo escribe en ella cuando usted aplica el resultado. El asunto se puede mover a otra carpeta o a GitHub después.")
            : T("A pasta de um clone do GitHub, Azure DevOps ou outro git. A tarefa vira um branch nxpilot/<id> no próprio repositório; juntar (merge/PR) é decisão sua.",
                "A clone folder from GitHub, Azure DevOps or another git host. The task becomes an nxpilot/<id> branch in the repository itself; merging (merge/PR) is up to you.",
                "La carpeta de un clon de GitHub, Azure DevOps u otro git. La tarea se convierte en una rama nxpilot/<id> en el propio repositorio; integrar (merge/PR) es decisión suya.");
        UpdateSummary();
        RefreshSubjectDefaultsState();
    }

    /// <summary>Recarrega os assuntos (depois de editar na janela Assuntos) mantendo o escolhido.</summary>
    private void LoadSubjects(string? select)
    {
        var subjects = ShadowRepo.LoadSubjects();
        SubjectBox.ItemsSource = subjects;
        if (SummarySubjectBox != null) SummarySubjectBox.ItemsSource = subjects; // a mesma lista no resumo
        SubjectBox.SelectedItem = subjects.FirstOrDefault(s => s.Name == select) ?? subjects.FirstOrDefault();
        UpdateSubjectHint();
    }

    private void OnSubjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummarySubjectBox != null && SummarySubjectBox.SelectedItem != SubjectBox.SelectedItem) SummarySubjectBox.SelectedItem = SubjectBox.SelectedItem;
        ApplySubjectDefaults();
        UpdateSubjectHint();
    }

    // ------------------------------------------------------------ padrão do assunto

    private SubjectDefaults? CurrentSubjectDefaults =>
        SelectedSubject is { } s && AppSettings.Load().SubjectDefaults.FirstOrDefault(d => string.Equals(d.Key, s.Path, StringComparison.OrdinalIgnoreCase)).Value is { } d ? d : null;

    /// <summary>Assunto com padrão gravado: agente, modelos e revisor voltam sozinhos.</summary>
    private void ApplySubjectDefaults()
    {
        if (AgentBox == null || ReviewerBox == null || CodexModelBox == null) return; // durante o InitializeComponent
        if (SelectedVersioning == VersioningMode.Local && CurrentSubjectDefaults is { } d)
        {
            Select(AgentBox, d.Executor);
            Select(ModelBox, d.Model ?? "");
            Select(ReviewerBox, d.Reviewer);
            if (d.CodexModel is { Length: > 0 } codex) CodexModelBox.Text = codex;
        }
        RefreshSubjectDefaultsState();
    }

    private void OnSaveSubjectDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubject is not { } subject) return;
        if (NXAgenticSelected) { ErrorText.Text = NXAgenticUnderConstruction; ErrorText.Visibility = Visibility.Visible; return; }
        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var defaults = new SubjectDefaults(SelectedExecutor.ToString(), string.IsNullOrEmpty(model) ? null : model,
            (ReviewerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(ReviewerKind.Codex),
            CodexModelBox.Text.Trim() is { Length: > 0 } codex ? codex : null, DateTime.Now);
        AppSettings.Update(s => s.SubjectDefaults[subject.Path] = defaults);
        RefreshSubjectDefaultsState();
    }

    private void OnClearSubjectDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSubject is not { } subject) return;
        AppSettings.Update(s =>
        {
            foreach (var key in s.SubjectDefaults.Keys.Where(k => string.Equals(k, subject.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                s.SubjectDefaults.Remove(key);
        });
        RefreshSubjectDefaultsState();
    }

    /// <summary>Mostra o padrão gravado do assunto (ou que não há) e se a tela está igual a ele.</summary>
    private void RefreshSubjectDefaultsState()
    {
        if (SubjectDefaultsCard == null) return;
        var local = SelectedVersioning == VersioningMode.Local && SelectedSubject != null;
        SubjectDefaultsCard.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        if (!local) return;
        var d = CurrentSubjectDefaults;
        ClearSubjectDefaultsButton.IsEnabled = d != null;
        if (d == null)
        {
            SubjectDefaultsState.Text = T($"\"{SelectedSubject!.Name}\" ainda não tem padrão: vale a última escolha.", $"\"{SelectedSubject!.Name}\" has no default yet: the last choice applies.", $"\"{SelectedSubject!.Name}\" aún no tiene predeterminado: vale la última elección.");
            SubjectDefaultsState.Foreground = (Brush)Application.Current.FindResource("SecondaryTextBrush");
            return;
        }
        var agent = d.Executor == nameof(ExecutorKind.Codex) ? "Codex" + (d.CodexModel is { } c ? $" ({c})" : "") : "Claude Code" + (d.Model is { } m ? $" ({m})" : "");
        var reviewer = d.Reviewer switch { "Claude" => "Claude", "None" => T("nenhum", "none", "ninguno"), _ => "Codex" + (d.CodexModel is { } c2 ? $" ({c2})" : "") };
        SubjectDefaultsState.Text = T($"✔ Padrão de \"{SelectedSubject!.Name}\": Agente {agent} · Revisor {reviewer} — gravado em {d.SavedAt:dd/MM HH:mm}.",
                                      $"✔ Default for \"{SelectedSubject!.Name}\": Agent {agent} · Reviewer {reviewer} — saved {d.SavedAt:MM/dd HH:mm}.",
                                      $"✔ Predeterminado de \"{SelectedSubject!.Name}\": Agente {agent} · Revisor {reviewer} — guardado el {d.SavedAt:dd/MM HH:mm}.");
        SubjectDefaultsState.Foreground = Brushes.SeaGreen;
    }

    /// <summary>Assunto trocado no resumo: vale o mesmo que trocar nas opções.</summary>
    private void OnSummarySubjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubjectBox != null && SummarySubjectBox.SelectedItem != SubjectBox.SelectedItem) SubjectBox.SelectedItem = SummarySubjectBox.SelectedItem;
    }

    private void UpdateSubjectHint()
    {
        if (SubjectHint == null) return;
        SubjectHint.Text = SelectedSubject is { } s
            ? s.Path + (s.WebUrl is { } url ? "  ·  GitHub: " + url : "")
            : T("Nenhum assunto ainda — crie um em \"Assuntos…\" e coloque os arquivos na pasta dele.", "No subjects yet — create one in \"Subjects…\" and put the files in its folder.", "Aún no hay asuntos — cree uno en \"Asuntos…\" y ponga los archivos en su carpeta.");
        UpdateSummary();
    }

    private void OnSubjectsClick(object sender, RoutedEventArgs e)
    {
        var window = new SubjectsWindow(SelectedSubject?.Name) { Owner = this };
        window.ShowDialog();
        LoadSubjects(window.SelectedName ?? SelectedSubject?.Name);
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Repositório git da tarefa", "Git repository for the task", "Repositorio git de la tarea") };
        if (Directory.Exists(RepoBox.Text)) dialog.InitialDirectory = RepoBox.Text;
        if (dialog.ShowDialog(this) == true) RepoBox.Text = dialog.FolderName;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim();
        var reviewer = Enum.Parse<ReviewerKind>((ReviewerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(ReviewerKind.Codex));
        var executor = SelectedExecutor;
        var versioning = SelectedVersioning;
        string? error = null;
        if (NXAgenticSelected) error = NXAgenticUnderConstruction;
        else if (executor == ExecutorKind.Codex ? !_codexInstalled : !_claudeInstalled)
            error = executor == ExecutorKind.Codex
                ? T("O Codex não foi encontrado nesta máquina (PATH, NXProject ou extensão do VS Code). Instale e faça login, ou escolha o Claude Code.",
                    "Codex was not found on this machine (PATH, NXProject or VS Code extension). Install it and sign in, or choose Claude Code.",
                    "Codex no se encontró en esta máquina (PATH, NXProject o extensión de VS Code). Instálelo e inicie sesión, o elija Claude Code.")
                : T("O Claude Code não foi encontrado nesta máquina. Instale e faça login, ou escolha o Codex.",
                    "Claude Code was not found on this machine. Install it and sign in, or choose Codex.",
                    "Claude Code no se encontró en esta máquina. Instálelo e inicie sesión, o elija Codex.");
        else if (versioning == VersioningMode.Local && SelectedSubject == null)
            error = T("Escolha um assunto (botão \"Assuntos…\").", "Choose a subject (\"Subjects…\" button).", "Elija un asunto (botón \"Asuntos…\").");
        else if (versioning == VersioningMode.Git && !Directory.Exists(repo)) error = T("Escolha a pasta de um repositório git.", "Choose a git repository folder.", "Elija la carpeta de un repositorio git.");
        else if (versioning == VersioningMode.Git && !Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            error = T("A pasta não tem .git — escolha a raiz de um repositório git, ou use o versionamento local.",
                      "The folder has no .git — choose the root of a git repository, or use local versioning.",
                      "La carpeta no tiene .git — elija la raíz de un repositorio git, o use el versionado local.");
        else if (TitleBox.Text.Trim().Length == 0) error = T("Dê um título à tarefa.", "Give the task a title.", "Dé un título a la tarea.");
        else if (IntentBox.Text.Trim().Length < 10) error = T("Descreva a intenção (o que deve ser feito).", "Describe the intent (what should be done).", "Describa la intención (qué se debe hacer).");
        else if (executor == ExecutorKind.Codex && CodexModelBox.Text.Trim().Length == 0)
            error = T("Com o Codex executando, informe o modelo do Codex (ex.: gpt-5.6-terra).", "With Codex executing, set the Codex model (e.g. gpt-5.6-terra).", "Con Codex como ejecutor, defina el modelo de Codex (p. ej., gpt-5.6-terra).");
        // Texto vindo do DevOps/GitHub não passa pelo MaxLength da caixa: o limite vale aqui.
        else if (TitleBox.Text.Trim().Length > TitleMax || IntentBox.Text.Trim().Length > IntentMax || CriteriaBox.Text.Trim().Length > CriteriaMax)
            error = T($"Passou do limite de caracteres (título {TitleMax:N0}, intenção {IntentMax:N0}, critérios {CriteriaMax:N0}): veja o contador em vermelho e resuma.",
                      $"Over the character limit (title {TitleMax:N0}, intent {IntentMax:N0}, criteria {CriteriaMax:N0}): see the red counter and shorten it.",
                      $"Supera el límite de caracteres (título {TitleMax:N0}, intención {IntentMax:N0}, criterios {CriteriaMax:N0}): vea el contador en rojo y resuma.");

        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            // Erro de "onde e com quem" (agente, assunto, repositório, modelo): mostra as opções.
            if (!ConfigLooksReady() || NXAgenticSelected) SetOptionsVisible(true);
            return;
        }

        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var codexModel = CodexModelBox.Text.Trim();
        var subject = versioning == VersioningMode.Local ? SelectedSubject : null;
        AppSettings.Update(s =>
        {
            if (subject == null) s.LastRepo = repo;
            else s.LastSubject = subject.Name;
            s.Versioning = versioning.ToString();
            s.LastModel = model;
            s.CodexModel = codexModel;
            s.Reviewer = reviewer.ToString();
            s.Executor = executor.ToString();
        });

        Result = new NewTaskInfo(subject?.Dir ?? repo, TitleBox.Text.Trim(), IntentBox.Text.Trim() + NXPilot.Planning.TaskCitations.Legend(_citations),
            CriteriaBox.Text.Trim().Length > 0 ? CriteriaBox.Text.Trim() : T("(não informados)", "(not provided)", "(no informado)"),
            model.Length > 0 ? model : null, reviewer, codexModel.Length > 0 ? codexModel : null, executor, versioning,
            subject?.Name, subject?.Path, AsTemplate: IsHookMode, WorkItem: _workItem,
            AutoFixFindings: AutoFixBox.IsChecked == true && reviewer != ReviewerKind.None);
        DialogResult = true;
    }
}
