// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NXPilot.WindowsUi;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Ler / monitorar um app escolhido pela pessoa (inclusive minimizado). A escolha vale como
/// autorização daquela leitura: o serviço é criado com uma política só desse processo.
/// Só lê — nunca clica, fecha ou digita.
/// </summary>
public partial class ReadAppWindow : Window
{
    private CancellationTokenSource? _monitor;

    public ReadAppWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { LoadApps(); LoadActions(); };
        Closing += (_, _) => _monitor?.Cancel(); // fechar a janela encerra o monitor
    }

    private void LoadApps()
    {
        AppsBox.Items.Clear();
        using var ui = new WindowsUiService(new WindowsUiPolicy([]));
        foreach (var w in ui.ListWindows(allowedOnly: false).OrderBy(w => w.ProcessName))
            AppsBox.Items.Add(new ComboBoxItem
            {
                Content = $"{w.ProcessName} — {w.Title}{(w.Minimized ? T("  (minimizado)", "  (minimized)", "  (minimizada)") : "")}",
                Tag = w,
            });
        if (AppsBox.Items.Count > 0) AppsBox.SelectedIndex = 0;
    }

    /// <summary>Só processos APROVADOS podem ser a ação do monitor.</summary>
    private void LoadActions()
    {
        ActionBox.Items.Clear();
        ActionBox.Items.Add(new ComboBoxItem { Content = T("Só avisar (nenhuma ação)", "Only alert (no action)", "Solo avisar (sin acción)") });
        foreach (var p in Services.UiProcessStore.Load().Where(p => p.IsApproved))
            ActionBox.Items.Add(new ComboBoxItem { Content = T($"Executar o processo \"{p.Name}\" ({p.ProcessName})", $"Run process \"{p.Name}\" ({p.ProcessName})", $"Ejecutar el proceso \"{p.Name}\" ({p.ProcessName})"), Tag = p });
        // Hooks armados: o alerta cria uma tarefa com o plano do hook (as linhas lidas viram o {texto}).
        foreach (var h in Services.TemplateStore.LoadAll().Where(h => h.Armed))
            ActionBox.Items.Add(new ComboBoxItem { Content = T($"⚡ Acionar o hook \"{h.Title}\"", $"⚡ Trigger hook \"{h.Title}\"", $"⚡ Disparar el hook \"{h.Title}\""), Tag = h });
        ActionBox.SelectedIndex = 0;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) { LoadApps(); LoadActions(); }

    private WindowInfo? Selected => (AppsBox.SelectedItem as ComboBoxItem)?.Tag as WindowInfo;

    /// <summary>Serviço autorizado SÓ para o app escolhido (a escolha é a autorização).</summary>
    private static WindowsUiService For(WindowInfo w) => new(new WindowsUiPolicy([w.ProcessName]));

    private async void OnReadClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } w) return;
        ReadButton.IsEnabled = false;
        Status(T("Lendo…", "Reading…", "Leyendo…"), Brushes.DimGray);
        try
        {
            var text = await Task.Run(() => { using var ui = For(w); return ui.ReadText(w); });
            TextBox.Text = text;
            Status(text.Length == 0
                ? T("O app não expôs texto pela acessibilidade (comum em imagens, PDF escaneado e áreas remotas).", "The app exposed no text through accessibility (common for images, scanned PDFs and remote desktops).", "La app no expuso texto por accesibilidad (común en imágenes, PDF escaneados y escritorios remotos).")
                : T($"{text.Length} caracteres lidos de {w.ProcessName}. Revise antes de usar.", $"{text.Length} characters read from {w.ProcessName}. Review before using.", $"{text.Length} caracteres leídos de {w.ProcessName}. Revise antes de usar."),
                text.Length == 0 ? Brushes.DarkOrange : Brushes.Green);
        }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
        finally { ReadButton.IsEnabled = true; }
    }

    private async void OnMonitorClick(object sender, RoutedEventArgs e)
    {
        if (_monitor != null) { _monitor.Cancel(); return; } // Parar
        if (Selected is not { } w) return;

        MonitorOptions options;
        try
        {
            options = new MonitorOptions(TimeSpan.FromSeconds(int.TryParse(IntervalBox.Text, out var s) ? s : 0),
                                         TimeSpan.FromMinutes(int.TryParse(DurationBox.Text, out var m) ? m : 0))
                      { Keywords = MonitorOptions.ParseKeywords(KeywordsBox.Text), Origin = OriginBox.Text.Trim() is { Length: > 0 } o ? o : null };
        }
        catch (ArgumentOutOfRangeException ex) { Status(ex.Message.Split(" (")[0], Brushes.Firebrick); return; }

        var action = (ActionBox.SelectedItem as ComboBoxItem)?.Tag as UiProcess;
        var hook = (ActionBox.SelectedItem as ComboBoxItem)?.Tag as Services.TaskTemplate;
        var where = options.Origin != null ? T($" na área \"{options.Origin}\"", $" in the \"{options.Origin}\" area", $" en el área \"{options.Origin}\"") : "";
        var what = options.Keywords.Count > 0
            ? T($"avisando quando aparecer{where}: {string.Join(", ", options.Keywords)}", $"alerting when it shows{where}: {string.Join(", ", options.Keywords)}", $"avisando cuando aparezca{where}: {string.Join(", ", options.Keywords)}")
            : T($"avisando a cada linha nova{where}", $"alerting on every new line{where}", $"avisando en cada línea nueva{where}");
        var then = action != null
            ? T($"\n\nAo disparar, executa o processo aprovado \"{action.Name}\" (no máximo 1 vez por leitura; exclusões pedem confirmação).",
                $"\n\nWhen it fires, runs the approved process \"{action.Name}\" (at most once per read; deletions ask for confirmation).",
                $"\n\nAl dispararse, ejecuta el proceso aprobado \"{action.Name}\" (como máximo una vez por lectura; las eliminaciones piden confirmación).")
            : hook != null
            ? T($"\n\nAo disparar, aciona o hook \"{hook.Title}\": cria uma tarefa com o plano dele, usando as linhas lidas como texto do evento. Disparos a menos de {hook.MinIntervalSeconds} s ou acima de {hook.MaxPerHour}/h pedem a sua aprovação.",
                $"\n\nWhen it fires, triggers the hook \"{hook.Title}\": creates a task with its plan, using the lines read as the event text. Triggers under {hook.MinIntervalSeconds} s apart or above {hook.MaxPerHour}/h ask for your approval.",
                $"\n\nAl dispararse, dispara el hook \"{hook.Title}\": crea una tarea con su plan, usando las líneas leídas como texto del evento. Los disparos a menos de {hook.MinIntervalSeconds} s o por encima de {hook.MaxPerHour}/h piden su aprobación.")
            : "";
        var confirm = T($"Monitorar {w.ProcessName} a cada {options.Interval.TotalSeconds:F0} s por {options.Duration.TotalMinutes:F0} min, {what}? Só leitura; para sozinho no fim do tempo.{then}",
                        $"Monitor {w.ProcessName} every {options.Interval.TotalSeconds:F0} s for {options.Duration.TotalMinutes:F0} min, {what}? Read-only; stops by itself when time is up.{then}",
                        $"¿Monitorear {w.ProcessName} cada {options.Interval.TotalSeconds:F0} s durante {options.Duration.TotalMinutes:F0} min, {what}? Solo lectura; se detiene solo al acabar el tiempo.{then}");
        if (MessageBox.Show(this, confirm, "NXPilot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _monitor = new CancellationTokenSource();
        MonitorButton.Content = T("Parar", "Stop", "Detener");
        MonitorLog.Items.Clear();
        var until = DateTime.Now + options.Duration;
        Status(T($"Monitorando até {until:HH:mm}…", $"Monitoring until {until:HH:mm}…", $"Monitoreando hasta las {until:HH:mm}…"), Brushes.DimGray);
        try
        {
            using var ui = For(w);
            await ui.MonitorAsync(w, options, new Progress<WindowsUiService.MonitorReading>(r =>
            {
                if (r.Alert)
                {
                    // Avisa com as linhas que dispararam; a área inteira (origem) fica no texto para usar.
                    foreach (var line in r.Matches)
                        MonitorLog.Items.Insert(0, new ListBoxItem { Content = $"🔔 {r.At:HH:mm:ss}  {line}", FontWeight = FontWeights.SemiBold });
                    TextBox.Text = r.Text;
                    System.Media.SystemSounds.Asterisk.Play();
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    if (action != null && !_acting) _ = ActAsync(action);
                    if (hook != null && Application.Current.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    {
                        MonitorLog.Items.Insert(0, T($"⚡ Hook \"{hook.Title}\" acionado", $"⚡ Hook \"{hook.Title}\" triggered", $"⚡ Hook \"{hook.Title}\" disparado"));
                        _ = vm.FireHookAsync(hook.Id, string.Join("\n", r.Matches), T($"monitor de apps · {w.ProcessName}", $"app monitor · {w.ProcessName}", $"monitor de apps · {w.ProcessName}"));
                    }
                }
                else
                    MonitorLog.Items.Insert(0, new ListBoxItem
                    {
                        Content = $"{r.At:HH:mm:ss}  #{r.Number}  " + (!r.OriginVisible ? T("origem não está na tela — aguardando", "origin is not on screen — waiting", "el origen no está en pantalla — esperando")
                            : r.Number == 1 ? T("primeira leitura (estado inicial)", "first read (initial state)", "primera lectura (estado inicial)") : T("nada novo", "nothing new", "nada nuevo")),
                        Foreground = Brushes.Gray,
                    });
            }), _monitor.Token);
            Status(T("Monitor encerrado.", "Monitor finished.", "Monitoreo concluido."), Brushes.DimGray);
        }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
        finally
        {
            _monitor?.Dispose();
            _monitor = null;
            MonitorButton.Content = T("Iniciar", "Start", "Iniciar");
        }
    }

    private bool _acting;

    /// <summary>Ação ao disparar: o processo aprovado, uma execução por vez (nunca em paralelo nem em loop).</summary>
    private async Task ActAsync(UiProcess process)
    {
        _acting = true;
        try
        {
            MonitorLog.Items.Insert(0, T($"▶ Executando o processo \"{process.Name}\"…", $"▶ Running process \"{process.Name}\"…", $"▶ Ejecutando el proceso \"{process.Name}\"…"));
            var results = await ProcessesWindow.RunAsync(this, process, line => MonitorLog.Items.Insert(0, "   " + line), _monitor?.Token ?? CancellationToken.None);
            MonitorLog.Items.Insert(0, results.All(x => x.Ok) ? T("✓ Processo concluído.", "✓ Process finished.", "✓ Proceso concluido.") : T("✗ Processo interrompido.", "✗ Process interrupted.", "✗ Proceso interrumpido."));
        }
        catch (Exception ex) { MonitorLog.Items.Insert(0, "✗ " + ex.Message); }
        finally { _acting = false; }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TextBox.Text); Status(T("Copiado.", "Copied.", "Copiado."), Brushes.Green); }
        catch { /* área de transferência ocupada */ }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Status(string text, Brush color) { StatusText.Text = text; StatusText.Foreground = color; }
}
