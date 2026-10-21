// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NXPilot.Desktop.Tutorial;
using NXPilot.Desktop.ViewModels;

namespace NXPilot.Desktop;

public partial class MainWindow : Window
{
    private readonly TutorialProgress _tutorial = TutorialProgress.Load();

    private MainViewModel Vm => (MainViewModel)DataContext;

    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        LanguageBox.SelectedIndex = NxText.IsSpanish ? 2 : NxText.IsEnglish ? 1 : 0;
        Closing += OnClosing;
        Tour.Ended += OnLessonEnded;
        // Hook: a execução de teste pede o {texto} que um evento traria.
        Vm.HookTestTextRequested = task => Views.TextPromptWindow.Ask(this,
            NxText.T("Execução de teste do hook", "Hook test run", "Ejecución de prueba del hook"),
            NxText.T("Digite o texto que um evento traria (entra no lugar de {texto}, como dado). Os riscos que você aprovar nesta execução ficam pré-aprovados nos disparos; depois dela, use ⚡ Armar hook.",
                     "Type the text an event would bring (it replaces {texto}, as data). The risks you approve in this run become pre-approved for triggers; afterwards, use ⚡ Arm hook.",
                     "Escriba el texto que traería un evento (reemplaza {texto}, como dato). Los riesgos que apruebe en esta ejecución quedan preaprobados en los disparos; después, use ⚡ Armar hook."),
            NxText.T("Executar teste", "Run test", "Ejecutar prueba"));
        Vm.Notifications.Raised += _ => FlashIfAway();
        Services.TeamsNotifier.Attach(Vm.Notifications, () => Vm.CurrentTask?.Info.SubjectFolder); // extensão Teams: os avisos escolhidos vão para o canal
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F1 && !Tour.IsRunning) { OpenHelp(); e.Handled = true; }
        };
        ContentRendered += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--shot")) await TryAutoScreenshotAsync();
            else if (_tutorial.ShowOnStartup && _tutorial.NextLesson() != null) OpenHelp(); // primeiro uso
        };
    }

    /// <summary>"?": abre o menu logo abaixo do botão (Lições · Sobre o NXPilot).</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        var menu = HelpButton.ContextMenu;
        menu.PlacementTarget = HelpButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnLessonsMenuClick(object sender, RoutedEventArgs e) => OpenHelp();

    private void OnLicenseClick(object sender, RoutedEventArgs e) => new Views.LicenseWindow { Owner = this }.ShowDialog();

    private void OnAboutClick(object sender, RoutedEventArgs e) => new Views.AboutWindow { Owner = this }.ShowDialog();

    private void OnNotificationsClick(object sender, RoutedEventArgs e) =>
        new Views.NotificationsWindow(Vm.Notifications) { Owner = this }.ShowDialog();

    /// <summary>
    /// Aviso novo com a janela minimizada ou em segundo plano: pisca o ícone na barra de tarefas
    /// até a pessoa voltar (FlashWindowEx; sem dependência de notificações do Windows).
    /// </summary>
    private void FlashIfAway()
    {
        if (IsActive && WindowState != WindowState.Minimized) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle,
            dwFlags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG: pisca até a janela voltar ao primeiro plano
        };
        FlashWindowEx(ref info);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>Histórico: abre uma tarefa salva na tela (somente leitura), clona para rodar de novo ou remove o worktree dela.</summary>
    private async void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.HistoryWindow(Vm.CurrentTask is { IsRunning: true }) { Owner = this };
        dialog.ShowDialog();
        if (dialog.ToOpen is { } record)
        {
            MainTabs.SelectedIndex = 0;
            await Vm.LoadTaskAsync(record);
        }
        else if (dialog.ToClone is { } clone)
            await StartNewTaskAsync(new Views.NewTaskWindow(clone.Info) { Owner = this });
    }

    /// <summary>Falhou antes do plano: abre a tarefa para editar (pode trocar o agente) e tenta de novo.</summary>
    private async void OnEditAndRetryClick(object sender, RoutedEventArgs e)
    {
        if (Vm.CurrentTask is not { CanEdit: true } task) return;
        var dialog = new Views.NewTaskWindow(task.Info, editing: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } info) return;
        await Vm.EditAndRetryAsync(info);
    }

    /// <summary>⚡ Hooks: tarefas em modo escuta (disparar, pausar, limites, remover).</summary>
    private void OnHooksClick(object sender, RoutedEventArgs e) => new Views.HooksWindow(Vm) { Owner = this }.ShowDialog();

    /// <summary>Work items do Azure DevOps: escolher um abre a Nova tarefa já preenchida.</summary>
    private async void OnWorkItemsClick(object sender, RoutedEventArgs e)
    {
        var list = new Views.WorkItemsWindow { Owner = this };
        list.ShowDialog();
        if (list.SelectedItems.Count > 0) await StartNewTaskAsync(new Views.NewTaskWindow(list.SelectedItems) { Owner = this });
    }

    /// <summary>Nova tarefa: assistente → plano do Claude Code na aba Plano (nada é escrito antes de aprovar).</summary>
    private async void OnNewTaskClick(object sender, RoutedEventArgs e)
    {
        if (Vm.CurrentTask is { IsRunning: true })
        {
            MessageBox.Show(this, NxText.T("Já há uma tarefa em andamento. Interrompa ou espere terminar.", "A task is already running. Stop it or wait for it to finish.", "Ya hay una tarea en curso. Interrúmpala o espere a que termine."),
                "NXPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await StartNewTaskAsync(new Views.NewTaskWindow { Owner = this });
    }

    private async Task StartNewTaskAsync(Views.NewTaskWindow dialog)
    {
        if (Vm.CurrentTask is { IsRunning: true }) return;
        if (dialog.ShowDialog() != true || dialog.Result is not { } info) return;
        MainTabs.SelectedIndex = 0; // aba Plano: onde o plano aparece e se aprova
        await Vm.StartTaskAsync(info);
    }

    /// <summary>
    /// Botões do protótipo que ainda não fazem nada: em vez de ficarem mudos (parece defeito),
    /// dizem o que vão fazer e do que dependem. Sai quando cada recurso for implementado.
    /// </summary>
    private void OnNotReadyClick(object sender, RoutedEventArgs e)
    {
        var (title, body) = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "nova" => (NxText.T("Nova tarefa — em construção", "New task — under construction", "Nueva tarea — en construcción"),
                NxText.T("Vai abrir o assistente de tarefa: você descreve a intenção e os critérios de aceite (ou escolhe uma Story do Azure DevOps), " +
                         "o Claude Code gera a spec e o plano em fases, e você aprova antes de qualquer agente começar.\n\n" +
                         "Depende do adaptador real do Claude Code (próximo passo do projeto). Hoje os agentes e fases da tela são dados de exemplo.",
                         "It will open the task wizard: you describe the intent and acceptance criteria (or pick an Azure DevOps Story), " +
                         "Claude Code generates the spec and the phased plan, and you approve it before any agent starts.\n\n" +
                         "It depends on the real Claude Code adapter (the project's next step). Today the agents and phases on screen are sample data.",
                         "Abrirá el asistente de tareas: usted describe la intención y los criterios de aceptación (o elige una Story de Azure DevOps), " +
                         "Claude Code genera la spec y el plan por fases, y usted lo aprueba antes de que cualquier agente comience.\n\n" +
                         "Depende del adaptador real de Claude Code (próximo paso del proyecto). Hoy los agentes y fases de la pantalla son datos de ejemplo.")),
            "historico" => (NxText.T("Histórico — em construção", "History — under construction", "Historial — en construcción"),
                NxText.T("Vai listar as tarefas anteriores com o log de auditoria de cada uma. Depende do registro de eventos em SQLite.",
                         "It will list previous tasks with each one's audit log. It depends on the SQLite event log.",
                         "Listará las tareas anteriores con el registro de auditoría de cada una. Depende del registro de eventos en SQLite.")),
            "notificacoes" => (NxText.T("Notificações — em construção", "Notifications — under construction", "Notificaciones — en construcción"),
                NxText.T("Vai juntar decisões pendentes, fases concluídas e mensagens prioritárias dos Canais, inclusive com a janela minimizada.",
                         "It will gather pending decisions, finished phases and priority messages from Channels, even with the window minimized.",
                         "Reunirá decisiones pendientes, fases concluidas y mensajes prioritarios de los Canales, incluso con la ventana minimizada.")),
            _ => (NxText.T("Work items — em construção", "Work items — under construction", "Work items — en construcción"),
                NxText.T("Vai trazer as Stories do Azure DevOps como origem das tarefas, reaproveitando a integração TFS do NXProject (edição comercial).",
                         "It will bring Azure DevOps Stories in as task sources, reusing the NXProject TFS integration (commercial edition).",
                         "Traerá las Stories de Azure DevOps como origen de las tareas, reutilizando la integración TFS de NXProject (edición comercial).")),
        };
        MessageBox.Show(this, body, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 1480×860 com zoom de 125% do Windows passa da altura útil (tela − barra de tarefas): a
    /// barra de título, com o X, nascia FORA da tela. Encolhe para caber e centraliza.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 16);
        Height = Math.Min(Height, area.Height - 16);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code } || code == NxText.Language) return;
        Services.LanguageService.Apply(code, save: true);
    }

    /// <summary>Com agentes rodando, pergunta: minimizar (seguem rodando) ou finalizar e sair.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || Environment.GetCommandLineArgs().Contains("--exit")) return;
        var running = Vm.RunningAgentCount;
        if (running == 0) return;

        e.Cancel = true;
        var dialog = new Views.CloseDialog(running) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case Views.CloseChoice.Minimize:
                WindowState = WindowState.Minimized;
                break;
            case Views.CloseChoice.ExitAndStopAgents:
                Vm.StopAllAgents();
                _closeConfirmed = true;
                Dispatcher.BeginInvoke(Close); // fecha fora do evento Closing atual
                break;
        }
    }

    // Segredos do WhatsApp: PasswordBox não tem binding (o texto nunca fica numa propriedade
    // observável); entrega ao ViewModel só quando a pessoa digita.
    private void OnSecretChanged(object sender, RoutedEventArgs e) =>
        Vm.Channels.SetSecrets(AccessTokenBox.Password, AppSecretBox.Password);

    private void OnSimulatorChecked(object sender, RoutedEventArgs e) => Vm.Channels.UseOfficialApi = false;

    /// <summary>Provedor do 3º degrau da cascata (LLaMA, Copilot, Azure, M365, OpenRouter).</summary>
    /// <summary>Extensões: Databricks SQL, Git (GitHub/Azure DevOps) e E-mail (Outlook/Gmail).</summary>
    private void OnExtensionsClick(object sender, RoutedEventArgs e) => new Views.ExtensionsWindow { Owner = this }.ShowDialog();

    /// <summary>Configurar: versão instalada, verificar atualização e voltar versão.</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e) => new Views.SettingsWindow { Owner = this }.ShowDialog();

    private void OnProviderClick(object sender, RoutedEventArgs e)
    {
        var win = new Views.AiProviderWindow(Vm.Lab.Provider) { Owner = this };
        if (win.ShowDialog() == true) Vm.Lab.ProviderSaved();
    }

    /// <summary>Abre o hub de lições; se a pessoa escolher uma, o tour começa na tela.</summary>
    private void OpenHelp(Lesson? justCompleted = null)
    {
        var help = new HelpWindow(_tutorial, justCompleted) { Owner = this };
        help.ShowDialog();
        if (help.SelectedLesson is { } lesson)
            Tour.Start(lesson, this, Vm);
    }

    /// <summary>Concluiu: marca e volta ao hub já sugerindo a próxima. Saiu no meio: só fecha.</summary>
    private void OnLessonEnded(Lesson lesson, bool completed)
    {
        if (!completed) return;
        _tutorial.Completed.Add(lesson.Id);
        _tutorial.Save();
        Dispatcher.BeginInvoke(() => OpenHelp(justCompleted: lesson));
    }

    /// <summary>
    /// Captura para documentação/CI:
    ///   NxPilot.exe --shot saida.png [--tab N] [--bench [--llama]] [--lesson id --step N] [--window about|license|history|workitems|newtask|hooks] [--exit]
    /// Renderiza a própria árvore visual (RenderTargetBitmap) — captura de tela externa
    /// não pega janelas WPF aceleradas por hardware de forma confiável.
    /// </summary>
    private async Task TryAutoScreenshotAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var shot = ArgValue(args, "--shot");
        if (shot == null) return;

        await Task.Delay(2500); // gate carrega o ONNX em segundo plano
        if (int.TryParse(ArgValue(args, "--tab"), out var tab))
            MainTabs.SelectedIndex = tab;

        if (args.Contains("--bench"))
        {
            Vm.Lab.BenchLlama = args.Contains("--llama");
            await Vm.Lab.RunBenchmarkCommand.ExecuteAsync(null);
            await Vm.Lab.TryClassifyCommand.ExecuteAsync(null);
        }

        if (args.Contains("--open-last-task") && Services.TaskStore.LoadAll().FirstOrDefault() is { } last)
        {
            await Vm.LoadTaskAsync(last);
            if (int.TryParse(ArgValue(args, "--tab"), out var t)) MainTabs.SelectedIndex = t;
            await Task.Delay(800);
        }

        if (args.Contains("--task-demo"))
        {
            await RunTaskDemoAsync(shot);
            if (args.Contains("--exit")) Close();
            return;
        }

        if (args.Contains("--channel-demo"))
        {
            Vm.Channels.PrioritySenders = "5511988887777"; // o "Diretor" dos payloads de exemplo
            await Vm.Channels.StartCommand.ExecuteAsync(null);
            for (var i = 0; i < 3; i++) Vm.Channels.EmitTestCommand.Execute(null);
        }

        if (ArgValue(args, "--lesson") is { } lessonId && LessonCatalog.All.FirstOrDefault(l => l.Id == lessonId) is { } lesson)
        {
            int.TryParse(ArgValue(args, "--step"), out var step);
            Tour.Start(lesson, this, Vm, step);
        }
        await Task.Delay(800);

        // --window about|license|history|risks|workitems|newtask: captura o Sobre, a Licença (modo primeira execução), o Histórico, os riscos de todas as tarefas, os Work items ou a Nova tarefa.
        if (ArgValue(args, "--window") is "about" or "license" or "history" or "risks" or "workitems" or "newtask" or "hooks")
        {
            Window win = ArgValue(args, "--window") switch
            {
                "about" => new Views.AboutWindow { Owner = this },
                "history" => new Views.HistoryWindow(taskRunning: false) { Owner = this },
                "risks" => new Views.RisksWindow(Services.TaskStore.LoadAll().Select(r => (r.Info.Title, (IReadOnlyList<RiskEntry>)ViewModels.TaskSession.FromRecord(r).Risks)).ToList()) { Owner = this },
                "workitems" => new Views.WorkItemsWindow { Owner = this },
                "newtask" => new Views.NewTaskWindow { Owner = this },
                "hooks" => new Views.HooksWindow(Vm) { Owner = this },
                _ => new Views.LicenseWindow { Owner = this, RequireAcceptance = true },
            };
            if (win is Views.NewTaskWindow newTask && args.Contains("--options")) newTask.ShowOptions();
            if (win is Views.NewTaskWindow citing && ArgValue(args, "--cite") is { } cited)
                citing.CiteForShot(NxText.T("Atualizar os totais de", "Update the totals of", "Actualizar los totales de"), cited.Split('|'));
            win.Show();
            await Task.Delay(win is Views.WorkItemsWindow ? 8000 : 800); // Work items consulta o DevOps/GitHub
            SaveVisual((FrameworkElement)win.Content, win, shot);
            win.Close();
            if (args.Contains("--exit")) Close();
            return;
        }

        // --provider Microsoft365: captura a janela de provedor nesse provedor (sem salvar nada).
        if (ArgValue(args, "--provider") is { } kindName && Enum.TryParse<NXPilot.Ai.AiProviderKind>(kindName, out var kind))
        {
            var copy = new NXPilot.Ai.AiProviderSettings { Kind = kind,
                Endpoint = NXPilot.Ai.AiProviderSettings.DefaultEndpoint(kind), Model = NXPilot.Ai.AiProviderSettings.DefaultModel(kind) };
            var win = new Views.AiProviderWindow(copy) { Owner = this };
            win.Show();
            await Task.Delay(800);
            SaveVisual((FrameworkElement)win.Content, win, shot);
            win.Close();
            if (args.Contains("--exit")) Close();
            return;
        }

        // --window readapp|processes: captura essas janelas (só abre; não lê nem executa nada).
        if (ArgValue(args, "--window") is { } which)
        {
            Window win = which == "processes" ? new Views.ProcessesWindow() : new Views.ReadAppWindow();
            win.Owner = this;
            win.Show();
            await Task.Delay(2500);
            SaveVisual((FrameworkElement)win.Content, win, shot);
            win.Close();
            if (args.Contains("--exit")) Close();
            return;
        }

        // --settings: captura a janela Configurar (consulta as versões no GitHub; não instala nada).
        if (args.Contains("--settings"))
        {
            var win = new Views.SettingsWindow { Owner = this };
            win.Show();
            await Task.Delay(5000);
            SaveVisual((FrameworkElement)win.Content, win, shot);
            win.Close();
            if (args.Contains("--exit")) Close();
            return;
        }

        // --extensions databricks|git|mail: captura a janela de Extensões nessa aba (sem salvar nada).
        if (ArgValue(args, "--extensions") is { } extTab)
        {
            var win = new Views.ExtensionsWindow(extTab) { Owner = this };
            win.Show();
            await Task.Delay(800);
            SaveVisual((FrameworkElement)win.Content, win, shot);
            win.Close();
            if (args.Contains("--exit")) Close();
            return;
        }

        SaveVisual((FrameworkElement)Content, this, shot);
        if (args.Contains("--exit")) Close();
    }

    /// <summary>
    /// Teste de ponta a ponta na interface, com o Claude Code REAL (Haiku, tarefa mínima):
    /// repositório descartável → plano → aprovação → pedido MÉDIO parado no painel (print) →
    /// aprovado → diff (print). Gera &lt;shot&gt;-plano.png, -decisao.png e -diff.png.
    /// </summary>
    private async Task RunTaskDemoAsync(string shot)
    {
        var repo = Path.Combine(Path.GetTempPath(), "nxpilot-ui-test-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(repo);
        foreach (var git in new[] { "init -q", "add -A", "-c user.name=NXPilot -c user.email=t@nx.local commit -q --allow-empty -m inicial" })
        {
            if (git == "add -A")
            {
                await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Teste\n");
                if (Environment.GetEnvironmentVariable("NXPILOT_DEMO_PBIP") == "1") await WriteSamplePbipAsync(repo);
            }
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", git) { WorkingDirectory = repo, CreateNoWindow = true, UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }
        string Name(string suffix) => Path.Combine(Path.GetDirectoryName(shot)!, Path.GetFileNameWithoutExtension(shot) + suffix + ".png");

        MainTabs.SelectedIndex = 0;
        var executor = Enum.TryParse<ExecutorKind>(Environment.GetEnvironmentVariable("NXPILOT_DEMO_EXECUTOR"), out var ex) ? ex : ExecutorKind.Claude;
        var info = Environment.GetEnvironmentVariable("NXPILOT_DEMO_PBIP") == "1"
            ? new NewTaskInfo(repo, NxText.T("Medida Total Vendas", "Total Sales measure", "Medida Total Ventas"),
                NxText.T("No modelo semântico do projeto Power BI, criar a medida Total Vendas = SUM(Vendas[Valor]) na tabela Vendas, com formato de moeda.",
                         "In the Power BI project semantic model, create the measure Total Sales = SUM(Vendas[Valor]) in the Vendas table, currency formatted.",
                         "En el modelo semántico del proyecto Power BI, crear la medida Total Ventas = SUM(Vendas[Valor]) en la tabla Vendas, con formato de moneda."),
                NxText.T("A medida existe no TMDL da tabela Vendas; nenhum JSON do relatório quebra; nada mais muda.", "The measure exists in the Vendas table TMDL; no report JSON breaks; nothing else changes.", "La medida existe en el TMDL de la tabla Vendas; ningún JSON del informe se rompe; nada más cambia."),
                "haiku", ReviewerKind.Codex, Environment.GetEnvironmentVariable("NXPILOT_CODEX_MODEL"), executor)
            : new NewTaskInfo(repo, NxText.T("Saudação e contador", "Greeting and counter", "Saludo y contador"),
                NxText.T("Fase 1: criar saudacao.txt com a linha Olá, NXPilot!. Fase 2: criar contar.ps1 que imprime quantas linhas saudacao.txt tem.",
                         "Phase 1: create saudacao.txt with the line Hello, NXPilot!. Phase 2: create contar.ps1 that prints how many lines saudacao.txt has.",
                         "Fase 1: crear saudacao.txt con la línea ¡Hola, NXPilot!. Fase 2: crear contar.ps1 que imprima cuántas líneas tiene saudacao.txt."),
                NxText.T("Os dois arquivos existem; contar.ps1 imprime 1; nada mais muda.", "Both files exist; contar.ps1 prints 1; nothing else changes.", "Los dos archivos existen; contar.ps1 imprime 1; nada más cambia."),
                "haiku", ReviewerKind.Codex, Environment.GetEnvironmentVariable("NXPILOT_CODEX_MODEL"), executor);
        await Vm.StartTaskAsync(info);
        await Task.Delay(500);
        SaveVisual((FrameworkElement)Content, this, Name("-plano"));
        MainTabs.SelectedIndex = 1;
        await Task.Delay(500);
        SaveVisual((FrameworkElement)Content, this, Name("-fases"));
        MainTabs.SelectedIndex = 0;

        var execution = Vm.ApprovePlanCommand.ExecuteAsync(null);
        var shotDecision = false;
        while (!execution.IsCompleted)
        {
            // Pedidos reais parados no gate: o primeiro vira print; todos são aprovados
            // (repositório descartável).
            foreach (var d in Vm.PendingDecisions.Where(d => d.Response != null).ToList())
            {
                if (!shotDecision)
                {
                    await Task.Delay(400);
                    SaveVisual((FrameworkElement)Content, this, Name("-decisao"));
                    shotDecision = true;
                }
                Vm.ApproveCommand.Execute(d);
            }
            await Task.Delay(500);
        }
        await execution;
        MainTabs.SelectedIndex = 1;
        await Task.Delay(600);
        SaveVisual((FrameworkElement)Content, this, Name("-fim-fases"));
        MainTabs.SelectedIndex = 2;
        await Task.Delay(600);
        SaveVisual((FrameworkElement)Content, this, Name("-fim-diff"));
        var wt = Vm.CurrentTask?.Worktree ?? "";
        var lines = new List<string>
        {
            $"estado={Vm.CurrentTask?.State}",
            $"fases={Vm.CurrentTask?.Phases.Count}",
            $"verificacao={Vm.CurrentTask?.Verification?.Reviewer} aprovado={Vm.CurrentTask?.Verification?.Approved} achados={Vm.CurrentTask?.Verification?.Findings.Count}",
            $"worktree={wt}",
            $"saudacao={File.Exists(Path.Combine(wt, "saudacao.txt"))} contar={File.Exists(Path.Combine(wt, "contar.ps1"))}",
            $"original_intocado={!File.Exists(Path.Combine(repo, "saudacao.txt"))}",
            $"historico={File.Exists(Path.Combine(Services.TaskStore.Folder, Vm.CurrentTask?.Id + ".json"))}",
        };
        lines.AddRange(Vm.Log.Select(l => $"{l.TimeText} {l.Source} {l.Kind} {l.Message}"));
        File.WriteAllLines(Name("-resultado").Replace(".png", ".txt"), lines);
    }

    /// <summary>Projeto PBIP mínimo para a demonstração (modelo em TMDL + relatório PBIR).</summary>
    private static async Task WriteSamplePbipAsync(string repo)
    {
        var tables = Path.Combine(repo, "Vendas.SemanticModel", "definition", "tables");
        var page = Path.Combine(repo, "Vendas.Report", "definition", "pages", "p1");
        Directory.CreateDirectory(tables);
        Directory.CreateDirectory(page);
        await File.WriteAllTextAsync(Path.Combine(repo, "Vendas.pbip"),
            "{\n  \"version\": \"1.0\",\n  \"artifacts\": [ { \"report\": { \"path\": \"Vendas.Report\" } } ]\n}\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "Vendas.SemanticModel", "definition", "model.tmdl"),
            "model Model\n\tculture: pt-BR\n\nref table Vendas\n");
        await File.WriteAllTextAsync(Path.Combine(tables, "Vendas.tmdl"),
            "table Vendas\n\n\tcolumn Data\n\t\tdataType: dateTime\n\t\tsourceColumn: Data\n\n\tcolumn Valor\n\t\tdataType: decimal\n\t\tsourceColumn: Valor\n\n" +
            "\tpartition Vendas = m\n\t\tmode: import\n\t\tsource = #table({\"Data\", \"Valor\"}, {})\n");
        await File.WriteAllTextAsync(Path.Combine(page, "page.json"),
            "{\n  \"name\": \"p1\",\n  \"displayName\": \"Página 1\",\n  \"displayOption\": \"FitToPage\"\n}\n");
    }

    private static void SaveVisual(FrameworkElement root, Window owner, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
