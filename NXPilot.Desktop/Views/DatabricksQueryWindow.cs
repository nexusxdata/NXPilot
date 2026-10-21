// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NXPilot.Databricks;
using static NXPilot.NxText;

namespace NXPilot.Desktop.Views;

/// <summary>
/// Extensão Databricks → Amostra de dados (não é extração): consulta de LEITURA com no máximo 50 registros,
/// mostra na vertical (cada coluna numa linha, cada registro numa coluna — como o modelo semântico do
/// Power BI) ou na horizontal; exporta para Excel (abas "Dados" e "Vertical").
/// </summary>
public sealed class DatabricksQueryWindow : Window
{
    private readonly DatabricksConnection _connection;
    private readonly TextBox _sql = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, Height = 120,
        FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly CheckBox _vertical = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _run = new() { Padding = new Thickness(14, 5, 14, 5) };
    private readonly Button _export = new() { Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6), Foreground = Brushes.DimGray };
    // Enquanto consulta: barra animada, tempo correndo e Cancelar (a consulta é cancelada no warehouse também).
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _running;
    private readonly DataGrid _grid = new()
    {
        IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All, HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEE)),
        VerticalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xDF)),
        FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, RowHeight = 30, EnableColumnVirtualization = true, EnableRowVirtualization = true,
        AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFB)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xE5)),
        ColumnHeaderStyle = HeaderStyle(),
    };

    /// <summary>Cabeçalho da grade: fundo azul-claro, negrito, com respiro.</summary>
    private static Style HeaderStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        style.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))));
        style.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 7, 10, 7)));
        style.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0xA3))));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        return style;
    }

    /// <summary>Célula de texto: respiro, reticências e alinhamento (números e datas à direita).</summary>
    private static Style CellText(bool right, bool bold = false, Binding? rightWhen = null)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(MaxWidthProperty, 300.0));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding("Text") { RelativeSource = RelativeSource.Self })); // texto cortado: inteiro no tooltip
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(HorizontalAlignmentProperty, right ? HorizontalAlignment.Right : HorizontalAlignment.Left));
        if (bold) { style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold)); style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x1F, 0x3B, 0x57)))); }
        if (rightWhen != null)
            style.Triggers.Add(new DataTrigger { Binding = rightWhen, Value = "R", Setters = { new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right) } });
        return style;
    }
    private readonly Dictionary<string, string> _headers = new();
    private readonly HashSet<string> _rightColumns = new();
    private string _lastTyped = "", _lastRun = "";
    private DateTime _lastWhen;
    private double _lastSeconds;
    private readonly ComboBox _recent = new() { MinWidth = 420, MaxWidth = 700, Margin = new Thickness(0, 0, 0, 6), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private SqlQueryResult? _result;

    /// <summary>Último resultado (print/teste).</summary>
    public SqlQueryResult? Result => _result;

    public DatabricksQueryWindow(DatabricksConnection connection, string? initialSql = null)
    {
        _connection = connection;
        Title = T("Databricks — Amostra de dados (até 50 registros)", "Databricks — Data sample (up to 50 rows)", "Databricks — Muestra de datos (hasta 50 registros)");
        Width = 1200; Height = 760; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        // Consulta inicial que roda: a última do histórico; senão os schemas do catálogo da conexão
        // (= USE CATALOG …; SHOW SCHEMAS, numa instrução só); sem catálogo, os catálogos.
        _sql.Text = initialSql
            ?? SampleHistory.Load().FirstOrDefault()?.Sql
            ?? (connection.Catalog is { Length: > 0 } cat ? $"SHOW SCHEMAS IN `{cat.Replace("`", "``")}`" : "SHOW CATALOGS");
        _run.Content = T("▶ Ver amostra (até 50 registros)", "▶ Show sample (up to 50 rows)", "▶ Ver muestra (hasta 50 registros)");
        _export.Content = T("Exportar para Excel…", "Export to Excel…", "Exportar a Excel…");
        _cancel.Content = T("Cancelar", "Cancel", "Cancelar");
        _cancel.Click += (_, _) => _running?.Cancel();
        Closed += (_, _) => _running?.Cancel();
        _vertical.Content = T("Vertical: cada coluna numa linha (como o modelo do Power BI)", "Vertical: each column in a row (like the Power BI model)", "Vertical: cada columna en una fila (como el modelo de Power BI)");
        _run.Click += async (_, _) => await RunAsync();
        FillRecent();
        _recent.SelectionChanged += (_, _) =>
        {
            if (_recent.SelectedItem is not ComboBoxItem { Tag: SampleHistory.Entry entry }) return;
            _sql.Text = entry.Sql;
            if (entry.Seconds is { } secs)
                Status(T($"Última execução: {SampleHistory.Format(secs)} ({entry.When:dd/MM HH:mm})", $"Last run: {SampleHistory.Format(secs)} ({entry.When:dd/MM HH:mm})", $"Última ejecución: {SampleHistory.Format(secs)} ({entry.When:dd/MM HH:mm})"), Brushes.DimGray);
        };
        _export.Click += (_, _) => Export();
        _vertical.Checked += (_, _) => Render();
        _vertical.Unchecked += (_, _) => Render();
        _grid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName == "c_align") { e.Cancel = true; return; } // só guia o alinhamento na vertical
            e.Column.Header = _headers.TryGetValue(e.PropertyName, out var h) ? h : e.PropertyName;
            if (e.Column is not DataGridTextColumn text) return;
            // Vertical: cada registro numa faixa (alterna branco / azul-claro) para não embolar com o vizinho.
            if (e.PropertyName.StartsWith('r') && int.TryParse(e.PropertyName[1..], out var reg) && reg % 2 == 1)
                text.CellStyle = new Style(typeof(DataGridCell)) { Setters = { new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xEE, 0xF4, 0xFB))) } };
            text.ElementStyle = e.PropertyName switch
            {
                "c_name" => CellText(right: false, bold: true),
                "c_type" => CellText(right: false),
                _ when e.PropertyName.StartsWith('r') => CellText(right: false, rightWhen: new Binding("c_align")), // vertical: pelo tipo da linha
                _ => CellText(right: _rightColumns.Contains(e.PropertyName)),                                 // horizontal: pelo tipo da coluna
            };
        };
        KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.F5) { e.Handled = true; await RunAsync(); } };

        var hint = new TextBlock
        {
            Text = T("Amostra para conferir os dados, não extração: só leitura (SELECT, WITH, SHOW, DESCRIBE…) e o LIMIT 50 é acrescentado sempre. F5 mostra a amostra.",
                     "A sample to check the data, not an extraction: read-only (SELECT, WITH, SHOW, DESCRIBE…) and LIMIT 50 is always added. F5 shows the sample.",
                     "Muestra para revisar los datos, no extracción: solo lectura (SELECT, WITH, SHOW, DESCRIBE…) y siempre se agrega LIMIT 50. F5 muestra la muestra."),
            Foreground = Brushes.DimGray, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 8),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_run);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_export);
        buttons.Children.Add(_vertical);

        var top = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        var recentRow = new StackPanel { Orientation = Orientation.Horizontal };
        recentRow.Children.Add(new TextBlock { Text = T("Recentes (só o SQL):", "Recent (SQL only):", "Recientes (solo el SQL):"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6), Foreground = Brushes.DimGray });
        recentRow.Children.Add(_recent);
        top.Children.Add(recentRow);
        // Exemplo em cinza enquanto a caixa está vazia (não é executado).
        var placeholder = new TextBlock
        {
            Text = T("Ex.: SELECT * FROM catálogo.schema.tabela", "E.g.: SELECT * FROM catalog.schema.table", "Ej.: SELECT * FROM catálogo.schema.tabla"),
            Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(6, 4, 0, 0), IsHitTestVisible = false,
            Visibility = _sql.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        _sql.TextChanged += (_, _) => placeholder.Visibility = _sql.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sqlBox = new Grid();
        sqlBox.Children.Add(_sql);
        sqlBox.Children.Add(placeholder);
        top.Children.Add(sqlBox);
        top.Children.Add(hint);
        top.Children.Add(buttons);
        top.Children.Add(_progress);
        top.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        _grid.Margin = new Thickness(16, 0, 16, 16);
        root.Children.Add(_grid);
        Content = root;
    }

    public async Task RunAsync()
    {
        var (sql, error) = SqlPreview.Prepare(_sql.Text);
        if (error != null) { Status(error, Brushes.Firebrick); return; }
        _run.IsEnabled = false;
        _running?.Cancel();
        using var cts = _running = new CancellationTokenSource();
        _progress.Visibility = _cancel.Visibility = Visibility.Visible;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Tick()
        {
            var s = (int)watch.Elapsed.TotalSeconds;
            Status(T($"Consultando… {s} s", $"Querying… {s} s", $"Consultando… {s} s")
                + (s >= 20 ? T(" — warehouse parado pode levar alguns minutos para ligar", " — a stopped warehouse may take a few minutes to start", " — un warehouse detenido puede tardar unos minutos en iniciar") : ""), Brushes.DimGray);
        }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Tick();
        Tick();
        timer.Start();
        try
        {
            using var svc = new DatabricksSqlService(_connection); // aprovador padrão: só leitura
            _result = await svc.QueryAsync(sql!, options: new SqlExecutionOptions { MaxRows = SqlPreview.MaxRows }, ct: cts.Token);
            timer.Stop();
            (_lastTyped, _lastRun, _lastWhen, _lastSeconds) = (_sql.Text.Trim(), sql!, DateTime.Now, watch.Elapsed.TotalSeconds);
            Render();
            Status(T($"{_result.Rows.Count} registro(s) · {_result.Columns.Count} coluna(s) · {_result.Elapsed.TotalSeconds:0.0} s · LIMIT {SqlPreview.MaxRows}",
                     $"{_result.Rows.Count} row(s) · {_result.Columns.Count} column(s) · {_result.Elapsed.TotalSeconds:0.0} s · LIMIT {SqlPreview.MaxRows}",
                     $"{_result.Rows.Count} registro(s) · {_result.Columns.Count} columna(s) · {_result.Elapsed.TotalSeconds:0.0} s · LIMIT {SqlPreview.MaxRows}"), Brushes.DarkGreen);
            _export.IsEnabled = _result.Columns.Count > 0;
            SampleHistory.Add(_sql.Text.Trim(), watch.Elapsed.TotalSeconds); // só o SQL e o tempo — os dados nunca são gravados
            FillRecent();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status(T($"Cancelada depois de {watch.Elapsed.TotalSeconds:0} s.", $"Cancelled after {watch.Elapsed.TotalSeconds:0} s.", $"Cancelada después de {watch.Elapsed.TotalSeconds:0} s."), Brushes.DarkOrange);
        }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
        finally
        {
            timer.Stop();
            if (ReferenceEquals(_running, cts)) _running = null;
            _progress.Visibility = _cancel.Visibility = Visibility.Collapsed;
            _run.IsEnabled = true;
        }
    }

    /// <summary>Monta a grade: vertical (coluna, tipo, registro 1…N) ou horizontal (uma linha por registro).</summary>
    private void Render()
    {
        if (_result is not { } r) return;
        var table = new DataTable();
        _headers.Clear();
        _rightColumns.Clear();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string Show(int col, string? value) => SqlValueFormat.Display(value, r.Columns[col].TypeText ?? r.Columns[col].TypeName, culture, T("Sim", "Yes", "Sí"), T("Não", "No", "No"));
        bool Right(int col) => SqlValueFormat.AlignRight(SqlValueFormat.KindOf(r.Columns[col].TypeText ?? r.Columns[col].TypeName));
        void Col(string key, string header) { table.Columns.Add(key); _headers[key] = header; }
        if (_vertical.IsChecked == true)
        {
            Col("c_name", T("Coluna", "Column", "Columna"));
            Col("c_type", T("Tipo", "Type", "Tipo"));
            table.Columns.Add("c_align"); // "R" = número/data: valores à direita nesta linha
            for (var i = 0; i < r.Rows.Count; i++) Col($"r{i}", T($"Registro {i + 1}", $"Row {i + 1}", $"Registro {i + 1}"));
            for (var c = 0; c < r.Columns.Count; c++)
            {
                var row = table.NewRow();
                row["c_name"] = r.Columns[c].Name;
                row["c_type"] = r.Columns[c].TypeText ?? r.Columns[c].TypeName;
                row["c_align"] = Right(c) ? "R" : "L";
                for (var i = 0; i < r.Rows.Count; i++) row[$"r{i}"] = Show(c, r.Rows[i].ElementAtOrDefault(c));
                table.Rows.Add(row);
            }
        }
        else
        {
            for (var c = 0; c < r.Columns.Count; c++) { Col($"k{c}", r.Columns[c].Name); if (Right(c)) _rightColumns.Add($"k{c}"); }
            foreach (var values in r.Rows)
            {
                var row = table.NewRow();
                for (var c = 0; c < r.Columns.Count; c++) row[$"k{c}"] = Show(c, values.ElementAtOrDefault(c));
                table.Rows.Add(row);
            }
        }
        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        _grid.ItemsSource = table.DefaultView;
    }

    /// <summary>Excel no MESMO formato da tela (vertical ou horizontal), cabeçalho com filtro. Números como número.</summary>
    private void Export()
    {
        if (_result is not { } r) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"amostra-databricks-{DateTime.Now:yyyyMMdd-HHmm}.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            NXPilot.Office.XlsxTable.Build(dialog.FileName, ExportSheets(r, _vertical.IsChecked == true, _lastTyped, _lastRun, _lastWhen, _lastSeconds));
            Status(T($"Exportado: {dialog.FileName}", $"Exported: {dialog.FileName}", $"Exportado: {dialog.FileName}"), Brushes.DarkGreen);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex) { Status(ex.Message, Brushes.Firebrick); }
    }

    /// <summary>
    /// Abas do Excel: a amostra no formato da tela (vertical ou horizontal), com números e datas como
    /// valores do Excel, e a aba "SQL" (consulta digitada, a que rodou com o LIMIT, quando, quantos
    /// registros e quanto tempo) — de onde vieram os dados. Público para o print/teste.
    /// </summary>
    public static IReadOnlyList<NXPilot.Office.XlsxSheet> ExportSheets(SqlQueryResult r, bool vertical, string typedSql, string runSql, DateTime when, double seconds)
    {
        NXPilot.Office.XlsxCell Cell(int col, string? value)
        {
            var type = r.Columns[col].TypeText ?? r.Columns[col].TypeName;
            var kind = SqlValueFormat.KindOf(type);
            var parsed = SqlValueFormat.Parse(value, kind);
            return kind switch
            {
                SqlValueKind.Integer => new(parsed, NXPilot.Office.XlsxCellKind.Integer),
                SqlValueKind.Decimal => new(parsed, NXPilot.Office.XlsxCellKind.Decimal, SqlValueFormat.DecimalPlaces(type) ?? (parsed is double d && d % 1 != 0 ? 2 : 0)),
                SqlValueKind.Date => new(parsed, NXPilot.Office.XlsxCellKind.Date),
                SqlValueKind.Timestamp => new(parsed, NXPilot.Office.XlsxCellKind.DateTime),
                SqlValueKind.Boolean => new(parsed, NXPilot.Office.XlsxCellKind.Boolean),
                _ => new(parsed, NXPilot.Office.XlsxCellKind.Text),
            };
        }

        NXPilot.Office.XlsxSheet sample;
        if (vertical)
        {
            var header = new List<string> { T("Coluna", "Column", "Columna"), T("Tipo", "Type", "Tipo") };
            header.AddRange(Enumerable.Range(1, r.Rows.Count).Select(i => T($"Registro {i}", $"Row {i}", $"Registro {i}")));
            var rows = new List<IReadOnlyList<NXPilot.Office.XlsxCell>>();
            for (var c = 0; c < r.Columns.Count; c++)
            {
                var line = new List<NXPilot.Office.XlsxCell>
                {
                    new(r.Columns[c].Name, NXPilot.Office.XlsxCellKind.Label),
                    new(r.Columns[c].TypeText ?? r.Columns[c].TypeName, NXPilot.Office.XlsxCellKind.Text),
                };
                line.AddRange(r.Rows.Select(values => Cell(c, values.ElementAtOrDefault(c))));
                rows.Add(line);
            }
            sample = new(T("Amostra (vertical)", "Sample (vertical)", "Muestra (vertical)"), header, rows, FrozenColumns: 2, BandFromColumn: 2); // cada registro numa faixa, como na tela
        }
        else
        {
            var rows = r.Rows.Select(values => (IReadOnlyList<NXPilot.Office.XlsxCell>)r.Columns.Select((_, c) => Cell(c, values.ElementAtOrDefault(c))).ToList()).ToList();
            sample = new(T("Amostra", "Sample", "Muestra"), r.Columns.Select(c => c.Name).ToList(), rows);
        }

        NXPilot.Office.XlsxCell Label(string text) => new(text, NXPilot.Office.XlsxCellKind.Label);
        NXPilot.Office.XlsxCell Wrapped(string text) => new(text, NXPilot.Office.XlsxCellKind.Text, Wrap: true);
        var info = new List<IReadOnlyList<NXPilot.Office.XlsxCell>>
        {
            new[] { Label(T("Consulta", "Query", "Consulta")), Wrapped(typedSql) },
            new[] { Label(T("Executada (amostra)", "Run (sample)", "Ejecutada (muestra)")), Wrapped(runSql) },
            new[] { Label(T("Data", "Date", "Fecha")), new NXPilot.Office.XlsxCell(when, NXPilot.Office.XlsxCellKind.DateTime) },
            new[] { Label(T("Registros", "Rows", "Registros")), new NXPilot.Office.XlsxCell((long)r.Rows.Count, NXPilot.Office.XlsxCellKind.Integer) },
            new[] { Label(T("Colunas", "Columns", "Columnas")), new NXPilot.Office.XlsxCell((long)r.Columns.Count, NXPilot.Office.XlsxCellKind.Integer) },
            new[] { Label(T("Tempo (s)", "Time (s)", "Tiempo (s)")), new NXPilot.Office.XlsxCell(Math.Round(seconds, 1), NXPilot.Office.XlsxCellKind.Decimal, 1) },
            new[] { Label(T("Observação", "Note", "Observación")), Wrapped(T($"Amostra de até {SqlPreview.MaxRows} registros para conferência — não é extração.", $"Sample of up to {SqlPreview.MaxRows} rows for review — not an extraction.", $"Muestra de hasta {SqlPreview.MaxRows} registros para revisión — no es una extracción.")) },
        };
        var sqlSheet = new NXPilot.Office.XlsxSheet("SQL", new[] { T("Campo", "Field", "Campo"), T("Valor", "Value", "Valor") }, info, MaxWidth: 110);
        return new[] { sample, sqlSheet };
    }

    private void Status(string text, Brush color) { _status.Text = text; _status.Foreground = color; }

    private void FillRecent()
    {
        _recent.Items.Clear();
        foreach (var entry in SampleHistory.Load())
        {
            var oneLine = string.Join(' ', entry.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var time = entry.Seconds is { } secs ? $"⏱ {SampleHistory.Format(secs)} · " : "";
            _recent.Items.Add(new ComboBoxItem { Content = time + (oneLine.Length > 110 ? oneLine[..110] + "…" : oneLine), Tag = entry, ToolTip = entry.Sql });
        }
        _recent.IsEnabled = _recent.Items.Count > 0;
    }
}

/// <summary>
/// Consultas recentes da Amostra de dados: guarda SÓ o SQL e quanto demorou da última vez (as 20
/// mais recentes), nunca os registros — o resultado pode ter informação confidencial. Fica na pasta
/// das extensões (local; o agente não lê).
/// </summary>
public static class SampleHistory
{
    public sealed record Entry(string Sql, double? Seconds = null, DateTime? When = null);

    private const int Max = 20;
    private static string FilePath => NXPilot.Integrations.ExtensionSettings.File("databricks-amostras.json");

    /// <summary>"0,8 s", "12 s", "2 min 05 s".</summary>
    public static string Format(double seconds) => seconds < 10 ? $"{seconds:0.0} s" : seconds < 60 ? $"{seconds:0} s" : $"{(int)(seconds / 60)} min {(int)seconds % 60:00} s";

    public static List<Entry> Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return new();
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(FilePath)) as System.Text.Json.Nodes.JsonArray ?? new();
            // Versão anterior gravava só a lista de SQLs (texto).
            return json.Select(n => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var sql) ? new Entry(sql)
                    : n?.Deserialize<Entry>()).Where(e => e is { Sql.Length: > 0 }).Select(e => e!).ToList();
        }
        catch { return new(); }
    }

    public static void Add(string sql, double seconds)
    {
        if (sql.Length == 0) return;
        var list = Load();
        list.RemoveAll(e => e.Sql == sql);
        list.Insert(0, new Entry(sql, Math.Round(seconds, 1), DateTime.Now));
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(list.Take(Max).ToList(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* sem gravar o histórico, a tela continua */ }
    }
}
