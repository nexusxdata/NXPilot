// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace NXPilot.Office;

/// <summary>
/// Excel pelo Open XML SDK (MIT, o mesmo do PowerPoint), sem Excel instalado nem Python:
/// resumo do arquivo, leitura em markdown com limite de linhas, escrita de células (valor,
/// fórmula, data) preservando o estilo, acréscimo de linhas (estende a tabela do Excel), aba
/// nova e planilha nova. Não calcula fórmulas: o arquivo fica marcado para recalcular ao abrir
/// (e o xlsx_recalculate, com o Excel instalado, grava os valores).
/// </summary>
public static class XlsxDocument
{
    /// <summary>Valor de célula vindo do agente: texto, número, booleano, null (limpa), "=fórmula" ou {"date": "2026-10-21"}.</summary>
    private const int MaxCellText = 200;

    // ================================================================ leitura

    /// <summary>Abas (com área usada e linhas), tabelas, nomes definidos e quantas fórmulas há.</summary>
    public static string Info(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var sb = new StringBuilder($"# {Path.GetFileName(path)}\n\n| Sheet | Used range | Rows | Formulas | Tables |\n|---|---|---|---|---|\n");
        foreach (var sheet in wb.Workbook!.Sheets!.Elements<Sheet>())
        {
            if (wb.GetPartById(sheet.Id!) is not WorksheetPart ws) { sb.AppendLine($"| {Esc(sheet.Name)} | (chart sheet) | | | |"); continue; }
            var cells = ws.Worksheet!.Descendants<Cell>().ToList();
            var (first, last) = UsedBounds(ws);
            var tables = ws.TableDefinitionParts.Select(t => $"{t.Table!.Name} {t.Table.Reference}").ToList();
            var hidden = sheet.State?.Value is { } st && st != SheetStateValues.Visible ? " (hidden)" : "";
            sb.AppendLine($"| {Esc(sheet.Name)}{hidden} | {(first == null ? "(empty)" : $"{Ref(first.Value)}:{Ref(last!.Value)}")} | " +
                          $"{(first == null ? 0 : last!.Value.Row - first.Value.Row + 1)} | {cells.Count(c => c.CellFormula != null)} | {string.Join(", ", tables)} |");
        }
        if (wb.Workbook!.DefinedNames?.Elements<DefinedName>().ToList() is { Count: > 0 } names)
            sb.Append("\nDefined names: ").AppendLine(string.Join("; ", names.Take(30).Select(n => $"{n.Name} = {n.Text}")));
        if (doc.WorkbookPart!.VbaProjectPart != null) sb.AppendLine("\nHas VBA macros (kept as they are; xlsx tools never run or change them).");
        return sb.ToString();
    }

    /// <summary>
    /// Uma aba em markdown (colunas pelas letras, linhas pelo número), no intervalo pedido ou na
    /// área usada, até <paramref name="maxRows"/> linhas. Fórmula aparece como "valor ⟨=FÓRMULA⟩".
    /// </summary>
    public static string Read(string path, string? sheetName, string? range, int maxRows = 100)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var (sheet, ws) = FindSheet(wb, sheetName);
        var (usedFirst, usedLast) = UsedBounds(ws);
        if (usedFirst == null) return $"Sheet '{sheet.Name}' is empty.";

        var (from, to) = range is { Length: > 0 } ? ParseRange(range) : (usedFirst.Value, usedLast!.Value);
        maxRows = Math.Clamp(maxRows, 1, 1000);
        var lastRow = Math.Min(to.Row, from.Row + maxRows - 1);
        var cols = Math.Min(to.Col - from.Col + 1, 60);
        var styles = new StyleInfo(wb);
        var sst = SharedStrings(wb);

        var sb = new StringBuilder($"Sheet '{sheet.Name}' · {Ref(from)}:{Ref(to)} (used range {Ref(usedFirst.Value)}:{Ref(usedLast!.Value)})\n\n");
        sb.Append("| # |");
        for (var c = 0; c < cols; c++) sb.Append(' ').Append(ColName(from.Col + c)).Append(" |");
        sb.Append("\n|---|").Append(string.Concat(Enumerable.Repeat("---|", cols))).Append('\n');

        var noCache = 0;
        var rows = ws.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>();
        var byRow = rows.Where(r => r.RowIndex != null && r.RowIndex.Value >= from.Row && r.RowIndex.Value <= lastRow)
            .ToDictionary(r => (int)r.RowIndex!.Value);
        for (var r = from.Row; r <= lastRow; r++)
        {
            var values = new string[cols];
            if (byRow.TryGetValue(r, out var row))
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = CellPos(cell, r).Col - from.Col;
                    if (col < 0 || col >= cols) continue;
                    var (text, cached) = Display(cell, sst, styles);
                    if (!cached) noCache++;
                    values[col] = Esc(text);
                }
            if (values.All(string.IsNullOrEmpty) && !byRow.ContainsKey(r) && r != from.Row) continue; // linha vazia: some
            sb.Append($"| {r} |");
            foreach (var v in values) sb.Append(' ').Append(v).Append(" |");
            sb.Append('\n');
        }
        if (lastRow < to.Row) sb.Append($"\n… {to.Row - lastRow} more row(s) not shown (rows {lastRow + 1}–{to.Row}). Ask for a range to see them.\n");
        if (to.Col - from.Col + 1 > cols) sb.Append($"\n… only the first {cols} columns are shown.\n");
        if (noCache > 0) sb.Append($"\n{noCache} formula cell(s) have no calculated value yet (shown as ⟨=…⟩ only): Excel computes them on open; xlsx_recalculate (if available) stores the values.\n");
        return sb.ToString();
    }

    // ================================================================ escrita

    /// <summary>Grava a matriz <paramref name="rows"/> a partir de <paramref name="start"/>. Devolve quantas células mudaram.</summary>
    public static int WriteCells(string path, string sheetName, string start, JsonArray rows)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wb = doc.WorkbookPart!;
        var (_, ws) = FindSheet(wb, sheetName);
        var origin = ParseCell(start);
        var n = WriteMatrix(wb, ws, origin, rows, styleFromAbove: false);
        AfterWrite(wb);
        return n;
    }

    /// <summary>Linhas novas depois da última usada, com o estilo da linha de cima; a tabela do Excel que termina ali cresce junto. Devolve a primeira linha gravada.</summary>
    public static int AppendRows(string path, string sheetName, JsonArray rows)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wb = doc.WorkbookPart!;
        var (_, ws) = FindSheet(wb, sheetName);
        var (first, last) = UsedBounds(ws);
        var startRow = last == null ? 1 : last.Value.Row + 1;
        var startCol = first?.Col ?? 1;

        // Tabela que termina na última linha usada: as linhas novas entram nela.
        foreach (var t in ws.TableDefinitionParts.Select(p => p.Table!))
        {
            var (tFrom, tTo) = ParseRange(t.Reference!.Value!);
            if (tTo.Row != startRow - 1) continue;
            startCol = tFrom.Col;
            var newRef = $"{Ref(tFrom)}:{Ref((tTo.Col, tTo.Row + rows.Count))}";
            t.Reference = newRef;
            if (t.AutoFilter != null) t.AutoFilter.Reference = newRef;
        }
        WriteMatrix(wb, ws, (startCol, startRow), rows, styleFromAbove: true);
        AfterWrite(wb);
        return startRow;
    }

    /// <summary>Aba nova (nome único, até 31 caracteres), opcionalmente já com linhas a partir de A1.</summary>
    public static void AddSheet(string path, string name, JsonArray? rows)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wb = doc.WorkbookPart!;
        var ws = NewSheet(wb, name);
        if (rows is { Count: > 0 }) WriteMatrix(wb, ws, (1, 1), rows, styleFromAbove: false);
        AfterWrite(wb);
    }

    /// <summary>
    /// Planilha nova: sheets = [{name, rows: [[...]], header?: true, widths?: [n...]}]. Com
    /// header, a 1ª linha fica em negrito com fundo, congelada e com filtro.
    /// </summary>
    public static int Build(string output, JsonArray sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("sheets: at least one sheet.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var doc = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook(new Sheets());
            var styles = wb.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = BaseStylesheet();
            foreach (var node in sheets)
            {
                var spec = node as JsonObject ?? throw new ArgumentException("Each sheet is an object {name, rows}.");
                var rows = spec["rows"] as JsonArray ?? new JsonArray();
                var ws = NewSheet(wb, spec["name"]?.ToString() ?? $"Sheet{wb.Workbook!.Sheets!.Count() + 1}");
                var header = spec["header"] is not JsonValue hv || !hv.TryGetValue<bool>(out var h) || h;
                WriteMatrix(wb, ws, (1, 1), rows, styleFromAbove: false);
                var colCount = rows.Select(r => (r as JsonArray)?.Count ?? 0).DefaultIfEmpty(0).Max();
                if (header && rows.Count > 0 && colCount > 0)
                {
                    foreach (var cell in ws.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().First().Elements<Cell>()) cell.StyleIndex = HeaderStyle;
                    var lastRef = $"{ColName(colCount)}{Math.Max(rows.Count, 2)}";
                    ws.Worksheet!.InsertAfter(new AutoFilter { Reference = $"A1:{lastRef}" }, ws.Worksheet!.GetFirstChild<SheetData>());
                    ws.Worksheet!.InsertAt(new SheetViews(new SheetView(new Pane
                    {
                        VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen,
                    }) { WorkbookViewId = 0, TabSelected = wb.Workbook!.Sheets!.Count() == 1 }), 0);
                }
                SetWidths(ws, rows, spec["widths"] as JsonArray, colCount);
            }
            AfterWrite(wb);
        }
        return sheets.Count;
    }

    public static IReadOnlyList<string> Validate(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        return new OpenXmlValidator().Validate(doc).Take(20).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
    }

    /// <summary>Células com erro (#REF!, #DIV/0!…) — para depois do recálculo.</summary>
    public static List<string> ErrorCells(string path, int max = 30)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var list = new List<string>();
        foreach (var sheet in wb.Workbook!.Sheets!.Elements<Sheet>())
            if (wb.GetPartById(sheet.Id!) is WorksheetPart ws)
                foreach (var c in ws.Worksheet!.Descendants<Cell>().Where(c => c.DataType?.Value == CellValues.Error))
                {
                    list.Add($"{sheet.Name}!{c.CellReference}: {c.CellValue?.Text}{(c.CellFormula != null ? $" (={c.CellFormula.Text})" : "")}");
                    if (list.Count >= max) return list;
                }
        return list;
    }

    // ================================================================ células

    private const uint HeaderStyle = 1, DateStyle = 2;

    private static int WriteMatrix(WorkbookPart wb, WorksheetPart ws, (int Col, int Row) origin, JsonArray rows, bool styleFromAbove)
    {
        var data = ws.Worksheet!.GetFirstChild<SheetData>() ?? ws.Worksheet!.AppendChild(new SheetData());
        GuardSharedFormulas(data, origin, rows);
        var styles = new StyleInfo(wb);
        var changed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var values = rows[i] as JsonArray ?? new JsonArray(rows[i]?.DeepClone());
            var rowIndex = origin.Row + i;
            var row = GetOrAddRow(data, rowIndex);
            var above = styleFromAbove ? data.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == (uint)(rowIndex - 1)) : null;
            for (var j = 0; j < values.Count; j++)
            {
                var col = origin.Col + j;
                var cell = GetOrAddCell(row, col, rowIndex);
                if (cell.StyleIndex == null && above?.Elements<Cell>().FirstOrDefault(c => CellPos(c, rowIndex - 1).Col == col) is { StyleIndex: { } s })
                    cell.StyleIndex = s.Value;
                SetValue(wb, cell, values[j], styles);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// A célula de origem de uma fórmula compartilhada guarda a fórmula das outras do intervalo:
    /// sobrescrevê-la sem cobrir o intervalo inteiro deixaria as outras sem fórmula (o Excel pede
    /// para "reparar"). Recusa antes de mudar qualquer coisa.
    /// </summary>
    private static void GuardSharedFormulas(SheetData data, (int Col, int Row) origin, JsonArray rows)
    {
        var lastRow = origin.Row + rows.Count - 1;
        var lastCol = origin.Col + rows.Select(r => (r as JsonArray)?.Count ?? 1).DefaultIfEmpty(1).Max() - 1;
        bool Inside((int Col, int Row) p)
        {
            if (p.Row < origin.Row || p.Row > lastRow || p.Col < origin.Col) return false;
            return p.Col - origin.Col < ((rows[p.Row - origin.Row] as JsonArray)?.Count ?? 1);
        }
        foreach (var row in data.Elements<Row>())
            foreach (var c in row.Elements<Cell>())
            {
                if (c.CellFormula?.FormulaType?.Value != CellFormulaValues.Shared || c.CellFormula.Reference?.Value is not { } shared) continue;
                var pos = CellPos(c, (int)(row.RowIndex?.Value ?? 0));
                if (!Inside(pos)) continue;
                var (from, to) = ParseRange(shared);
                if (from.Col < origin.Col || to.Col > lastCol || from.Row < origin.Row || to.Row > lastRow)
                    throw new InvalidOperationException($"{Ref(pos)} holds the shared formula of {shared} (={c.CellFormula.Text}). " +
                        $"Write the whole range {shared} in the same call, or leave {Ref(pos)} out.");
            }
    }

    private static void SetValue(WorkbookPart wb, Cell cell, JsonNode? value, StyleInfo styles)
    {
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;
        switch (value)
        {
            case null:
                return; // limpa (o estilo fica)
            case JsonObject o when o["date"] is { } d:
                var date = DateTime.Parse(d.ToString(), CultureInfo.InvariantCulture);
                cell.CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture));
                if (!styles.IsDate(cell.StyleIndex?.Value ?? 0)) cell.StyleIndex = styles.DateVariantOf(cell.StyleIndex?.Value ?? 0);
                return;
            case JsonObject o when o["formula"] is { } f:
                cell.CellFormula = new CellFormula(f.ToString().TrimStart('='));
                return;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                cell.CellValue = new CellValue(Number(v).ToString("R", CultureInfo.InvariantCulture));
                return;
            case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(v.GetValue<bool>() ? "1" : "0");
                return;
        }
        var text = value.ToString();
        if (text.StartsWith('=') && text.Length > 1)
        {
            cell.CellFormula = new CellFormula(text[1..]);
            return;
        }
        cell.DataType = CellValues.SharedString;
        cell.CellValue = new CellValue(SharedStringIndex(wb, text).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Número do JSON como double (vale para int, long, decimal e o JSON lido do agente).</summary>
    private static double Number(JsonValue v) => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Row GetOrAddRow(SheetData data, int index)
    {
        var after = (Row?)null;
        foreach (var r in data.Elements<Row>())
        {
            var ri = (int)(r.RowIndex?.Value ?? 0);
            if (ri == index) return r;
            if (ri > index) break;
            after = r;
        }
        var row = new Row { RowIndex = (uint)index };
        if (after == null) data.InsertAt(row, 0); else after.InsertAfterSelf(row);
        return row;
    }

    private static Cell GetOrAddCell(Row row, int col, int rowIndex)
    {
        var after = (Cell?)null;
        foreach (var c in row.Elements<Cell>())
        {
            var cc = CellPos(c, rowIndex).Col;
            if (cc == col) { c.CellReference ??= Ref((col, rowIndex)); return c; }
            if (cc > col) break;
            after = c;
        }
        var cell = new Cell { CellReference = Ref((col, rowIndex)) };
        if (after == null) row.InsertAt(cell, 0); else after.InsertAfterSelf(cell);
        return cell;
    }

    /// <summary>Depois de gravar: Excel recalcula ao abrir e a cadeia de cálculo antiga sai (evita o "reparar arquivo").</summary>
    private static void AfterWrite(WorkbookPart wb)
    {
        var calc = wb.Workbook!.GetFirstChild<CalculationProperties>();
        if (calc == null)
        {
            // Posição do esquema: depois de sheets/functionGroups/externalReferences/definedNames.
            calc = new CalculationProperties();
            OpenXmlElement anchor = (OpenXmlElement?)wb.Workbook!.GetFirstChild<DefinedNames>() ?? (OpenXmlElement?)wb.Workbook!.GetFirstChild<ExternalReferences>()
                ?? (OpenXmlElement?)wb.Workbook!.GetFirstChild<FunctionGroups>() ?? wb.Workbook!.Sheets!;
            anchor.InsertAfterSelf(calc);
        }
        calc.FullCalculationOnLoad = true;
        if (wb.CalculationChainPart != null) wb.DeletePart(wb.CalculationChainPart);
        wb.Workbook!.Save();
    }

    private static WorksheetPart NewSheet(WorkbookPart wb, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 31 || name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0)
            throw new ArgumentException($"Invalid sheet name '{name}': 1 to 31 characters, without [ ] : * ? / \\.");
        var sheets = wb.Workbook!.Sheets ?? wb.Workbook!.AppendChild(new Sheets());
        if (sheets.Elements<Sheet>().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There is already a sheet named '{name}'.");
        var ws = wb.AddNewPart<WorksheetPart>();
        ws.Worksheet = new Worksheet(new SheetData());
        var id = sheets.Elements<Sheet>().Select(s => s.SheetId?.Value ?? 0).DefaultIfEmpty(0u).Max() + 1;
        sheets.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = id, Name = name });
        return ws;
    }

    private static void SetWidths(WorksheetPart ws, JsonArray rows, JsonArray? widths, int colCount)
    {
        if (colCount == 0) return;
        var columns = new Columns();
        for (var c = 0; c < colCount; c++)
        {
            double width = widths != null && c < widths.Count && widths[c] is JsonValue wv && wv.GetValueKind() == JsonValueKind.Number ? Number(wv)
                : Math.Clamp(rows.Take(200).Select(r => (r as JsonArray) is { } a && c < a.Count ? (a[c]?.ToString().Length ?? 0) : 0).DefaultIfEmpty(0).Max() + 2, 8, 60);
            columns.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = width, CustomWidth = true });
        }
        ws.Worksheet!.InsertBefore(columns, ws.Worksheet!.GetFirstChild<SheetData>());
    }

    private static Stylesheet BaseStylesheet() => new(
        new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                  new Font(new Bold(), new FontSize { Val = 11 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" })) { Count = 2 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }),
                  new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                  new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1F4E79" }) { PatternType = PatternValues.Solid })) { Count = 3 },
        new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
        new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
        new CellFormats(
            new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 },
            new CellFormat { NumberFormatId = 0, FontId = 1, FillId = 2, BorderId = 0, FormatId = 0, ApplyFont = true, ApplyFill = true },
            new CellFormat { NumberFormatId = 14, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0, ApplyNumberFormat = true }) { Count = 3 },
        new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });

    // ================================================================ strings e estilos

    private static List<string> SharedStrings(WorkbookPart wb) =>
        wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? new();

    private static int SharedStringIndex(WorkbookPart wb, string text)
    {
        var part = wb.SharedStringTablePart ?? wb.AddNewPart<SharedStringTablePart>();
        part.SharedStringTable ??= new SharedStringTable();
        var i = 0;
        foreach (var item in part.SharedStringTable.Elements<SharedStringItem>())
        {
            if (item.InnerText == text && item.Text != null) return i;
            i++;
        }
        var t = new Text(text);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))) t.Space = SpaceProcessingModeValues.Preserve;
        part.SharedStringTable.AppendChild(new SharedStringItem(t));
        return i;
    }

    /// <summary>Formatos de data (para ler como data e não como número de série).</summary>
    private sealed class StyleInfo(WorkbookPart wb)
    {
        private readonly Stylesheet? _ss = wb.WorkbookStylesPart?.Stylesheet;

        public bool IsDate(uint styleIndex)
        {
            if (_ss?.CellFormats?.Elements<CellFormat>().ElementAtOrDefault((int)styleIndex) is not { } xf) return false;
            var id = xf.NumberFormatId?.Value ?? 0;
            if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) return true;
            var code = _ss.NumberingFormats?.Elements<NumberingFormat>().FirstOrDefault(n => n.NumberFormatId?.Value == id)?.FormatCode?.Value;
            if (code == null) return false;
            code = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
            return Regex.IsMatch(code, "[dyDY]") || Regex.IsMatch(code, "[hH].*[mM]|[mM].*[sS]");
        }

        /// <summary>Mesmo estilo (fonte, fundo, borda) com formato de data dd/mm/aaaa — cria se não existir.</summary>
        public uint DateVariantOf(uint styleIndex)
        {
            if (_ss == null) return 0;
            _ss.CellFormats ??= new CellFormats(new CellFormat());
            var list = _ss.CellFormats.Elements<CellFormat>().ToList();
            var baseXf = list.ElementAtOrDefault((int)styleIndex) ?? new CellFormat();
            for (var i = 0; i < list.Count; i++)
                if (list[i].NumberFormatId?.Value == 14 && list[i].FontId?.Value == baseXf.FontId?.Value
                    && list[i].FillId?.Value == baseXf.FillId?.Value && list[i].BorderId?.Value == baseXf.BorderId?.Value)
                    return (uint)i;
            var xf = (CellFormat)baseXf.CloneNode(true);
            xf.NumberFormatId = 14;
            xf.ApplyNumberFormat = true;
            _ss.CellFormats.Append(xf);
            _ss.CellFormats.Count = (uint)list.Count + 1;
            return (uint)list.Count;
        }
    }

    /// <summary>Texto para o agente e se a fórmula tem valor calculado guardado.</summary>
    private static (string Text, bool Cached) Display(Cell cell, List<string> sst, StyleInfo styles)
    {
        var raw = cell.CellValue?.Text;
        string value;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var si)) value = si < sst.Count ? sst[si] : "";
        else if (cell.DataType?.Value == CellValues.InlineString) value = cell.InlineString?.InnerText ?? "";
        else if (cell.DataType?.Value == CellValues.Boolean) value = raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw ?? "";
        else if (raw != null && cell.DataType == null && styles.IsDate(cell.StyleIndex?.Value ?? 0)
                 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2958466)
        {
            var dt = DateTime.FromOADate(serial);
            value = dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : serial < 1 ? dt.ToString("HH:mm:ss") : dt.ToString("yyyy-MM-dd HH:mm");
        }
        else value = raw ?? "";
        if (value.Length > MaxCellText) value = value[..MaxCellText] + "…";
        value = value.Replace("\r", " ").Replace("\n", " ");
        if (cell.CellFormula is not { } f) return (value, true);
        var formula = "⟨=" + (f.Text.Length > 0 ? f.Text : "shared") + "⟩";
        return raw == null ? (formula, false) : ($"{value} {formula}", true);
    }

    // ================================================================ endereços

    private static (Sheet Sheet, WorksheetPart Part) FindSheet(WorkbookPart wb, string? name)
    {
        var sheets = wb.Workbook!.Sheets!.Elements<Sheet>().ToList();
        var sheet = name is { Length: > 0 }
            ? sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"Sheet '{name}' not found. Sheets: {string.Join(", ", sheets.Select(s => s.Name?.Value))}.")
            : sheets.FirstOrDefault(s => wb.GetPartById(s.Id!) is WorksheetPart) ?? throw new ArgumentException("The workbook has no worksheet.");
        return wb.GetPartById(sheet.Id!) is WorksheetPart ws ? (sheet, ws) : throw new ArgumentException($"'{sheet.Name}' is a chart sheet, not a worksheet.");
    }

    /// <summary>Primeira e última célula com conteúdo (pelas células, não pela dimensão gravada, que costuma mentir).</summary>
    private static ((int Col, int Row)? First, (int Col, int Row)? Last) UsedBounds(WorksheetPart ws)
    {
        int minC = int.MaxValue, minR = int.MaxValue, maxC = 0, maxR = 0;
        foreach (var row in ws.Worksheet!.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            var r = (int)(row.RowIndex?.Value ?? 0);
            foreach (var c in row.Elements<Cell>())
            {
                if (c.CellValue == null && c.CellFormula == null && c.InlineString == null) continue;
                var p = CellPos(c, r);
                minC = Math.Min(minC, p.Col); maxC = Math.Max(maxC, p.Col);
                minR = Math.Min(minR, p.Row); maxR = Math.Max(maxR, p.Row);
            }
        }
        return maxR == 0 ? (null, null) : ((minC, minR), (maxC, maxR));
    }

    private static (int Col, int Row) CellPos(Cell c, int row) => c.CellReference?.Value is { } r ? ParseCell(r) : (0, row);

    public static (int Col, int Row) ParseCell(string reference)
    {
        var m = Regex.Match(reference.Trim().Replace("$", ""), "^([A-Za-z]{1,3})([0-9]{1,7})$");
        if (!m.Success) throw new ArgumentException($"Invalid cell '{reference}' (expected like B3).");
        var col = 0;
        foreach (var ch in m.Groups[1].Value.ToUpperInvariant()) col = col * 26 + (ch - 'A' + 1);
        return (col, int.Parse(m.Groups[2].Value));
    }

    private static ((int Col, int Row) From, (int Col, int Row) To) ParseRange(string range)
    {
        var parts = range.Split(':');
        var a = ParseCell(parts[0]);
        var b = parts.Length > 1 ? ParseCell(parts[1]) : a;
        return ((Math.Min(a.Col, b.Col), Math.Min(a.Row, b.Row)), (Math.Max(a.Col, b.Col), Math.Max(a.Row, b.Row)));
    }

    public static string ColName(int col)
    {
        var s = "";
        for (; col > 0; col = (col - 1) / 26) s = (char)('A' + (col - 1) % 26) + s;
        return s;
    }

    private static string Ref((int Col, int Row) p) => $"{ColName(p.Col)}{p.Row}";

    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|");
}
