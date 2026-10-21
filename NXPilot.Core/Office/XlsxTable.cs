// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace NXPilot.Office;

/// <summary>Formato de uma célula da tabela de apresentação.</summary>
public enum XlsxCellKind { Text, Label, Integer, Decimal, Date, DateTime, Boolean }

/// <summary>Uma célula: valor tipado (texto, long/double, DateTime, bool ou nulo) e o formato.</summary>
public readonly record struct XlsxCell(object? Value, XlsxCellKind Kind, int Decimals = 2, bool Wrap = false);

/// <summary>Uma aba: cabeçalho, linhas e quantas colunas ficam congeladas à esquerda.</summary>
/// <param name="BandFromColumn">A partir desta coluna (0 = A), uma coluna sim, outra não, com fundo azul-claro — cada registro numa faixa (vista transposta).</param>
public sealed record XlsxSheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<XlsxCell>> Rows, int FrozenColumns = 0, double? MaxWidth = null,
    int? BandFromColumn = null);

/// <summary>
/// Planilha de apresentação (para o time de negócio): cabeçalho azul com filtro, bordas finas, números
/// e datas como valores do Excel com formato (o separador segue o idioma do Excel de quem abre),
/// números e datas à direita, largura das colunas pelo conteúdo e painéis congelados.
/// </summary>
public static class XlsxTable
{
    // Índices dos formatos de célula (CellFormats) da folha de estilos abaixo.
    private const uint Normal = 0, Header = 1, Label = 2, Integer = 3, Date = 4, DateTime = 5, TextCenter = 6, DecimalBase = 7, Wrap = 14; // 7..13 = 0..6 casas
    /// <summary>Os mesmos formatos com fundo azul-claro (faixa): índice + Banded.</summary>
    private const uint Banded = 15;

    public static void Build(string path, IReadOnlyList<XlsxSheet> sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        wb.AddNewPart<WorkbookStylesPart>().Stylesheet = Styles();
        var list = new Sheets();
        uint id = 1;
        foreach (var sheet in sheets)
        {
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = Worksheet(sheet, first: id == 1);
            list.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = id++, Name = SafeSheetName(sheet.Name) });
        }
        wb.Workbook.AppendChild(list);
        wb.Workbook.Save();
    }

    private static Worksheet Worksheet(XlsxSheet sheet, bool first)
    {
        var (header, rows, frozenColumns) = (sheet.Header, sheet.Rows, sheet.FrozenColumns);
        var data = new SheetData();

        var widths = header.Select(h => (double)Math.Max(8, h.Length + 3)).ToArray();
        var headerRow = new Row { RowIndex = 1, Height = 22, CustomHeight = true };
        for (var c = 0; c < header.Count; c++) headerRow.Append(TextCell(c, 1, header[c], Header));
        data.Append(headerRow);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new Row { RowIndex = (uint)(r + 2) };
            // Texto quebrado (ex.: o SQL): altura pelas linhas — o Excel não recalcula sozinho ao abrir.
            var wrapLines = rows[r].Where(x => x.Wrap && x.Value is string).Select(x => ((string)x.Value!).Split('\n').Sum(l => 1 + l.Length / 110)).DefaultIfEmpty(0).Max();
            if (wrapLines > 1) { row.Height = Math.Min(400, wrapLines * 14.5 + 4); row.CustomHeight = true; }
            for (var c = 0; c < rows[r].Count && c < header.Count; c++)
            {
                var cell = rows[r][c];
                var xc = Cell(c, r + 2, cell);
                if (sheet.BandFromColumn is { } band && c >= band && (c - band) % 2 == 1 && xc.StyleIndex?.Value is { } st && st != Header && st != Label)
                    xc.StyleIndex = st + Banded;
                row.Append(xc);
                var shown = cell.Value switch
                {
                    null => 0,
                    System.DateTime d => d.TimeOfDay == TimeSpan.Zero ? 10 : 16,
                    double dd => dd.ToString("#,##0." + new string('0', Math.Max(0, cell.Decimals)), CultureInfo.InvariantCulture).Length,
                    long l => l.ToString("#,##0", CultureInfo.InvariantCulture).Length,
                    var v => v.ToString()?.Length ?? 0,
                };
                widths[c] = Math.Max(widths[c], Math.Min(sheet.MaxWidth ?? 60, shown + 3));
            }
            data.Append(row);
        }

        var columns = new Columns();
        for (var c = 0; c < widths.Length; c++) columns.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = widths[c], CustomWidth = true });
        var lastRef = $"{XlsxDocument.ColName(Math.Max(1, header.Count))}{Math.Max(2, rows.Count + 1)}";
        var frozenCell = $"{XlsxDocument.ColName(frozenColumns + 1)}2";
        var pane = new Pane { VerticalSplit = 1, TopLeftCell = frozenCell, ActivePane = frozenColumns > 0 ? PaneValues.BottomRight : PaneValues.BottomLeft, State = PaneStateValues.Frozen };
        if (frozenColumns > 0) pane.HorizontalSplit = frozenColumns;
        return new Worksheet(
            new SheetViews(new SheetView(pane) { WorkbookViewId = 0, TabSelected = first, ShowGridLines = false }),
            new SheetFormatProperties { DefaultRowHeight = 18 },
            columns, data,
            new AutoFilter { Reference = $"A1:{lastRef}" });
    }

    private static Cell Cell(int col, int row, XlsxCell cell)
    {
        var reference = $"{XlsxDocument.ColName(col + 1)}{row}";
        switch (cell.Value)
        {
            case null:
                return new Cell { CellReference = reference, StyleIndex = cell.Kind == XlsxCellKind.Label ? Label : Normal };
            case long l:
                return new Cell { CellReference = reference, CellValue = new CellValue(l.ToString(CultureInfo.InvariantCulture)), StyleIndex = Integer };
            case double d:
                return new Cell { CellReference = reference, CellValue = new CellValue(d.ToString("R", CultureInfo.InvariantCulture)), StyleIndex = DecimalBase + (uint)Math.Clamp(cell.Decimals, 0, 6) };
            case System.DateTime dt:
                return new Cell { CellReference = reference, CellValue = new CellValue(dt.ToOADate().ToString(CultureInfo.InvariantCulture)),
                    StyleIndex = cell.Kind == XlsxCellKind.Date || dt.TimeOfDay == TimeSpan.Zero ? Date : DateTime };
            case bool b:
                return TextCell(col, row, b ? NxText.T("Sim", "Yes", "Sí") : NxText.T("Não", "No", "No"), TextCenter);
            default:
                return TextCell(col, row, cell.Value.ToString() ?? "", cell.Kind == XlsxCellKind.Label ? Label : cell.Wrap ? Wrap : Normal);
        }
    }

    private static Cell TextCell(int col, int row, string text, uint style) =>
        new(new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
        { CellReference = $"{XlsxDocument.ColName(col + 1)}{row}", DataType = CellValues.InlineString, StyleIndex = style };

    private static string SafeSheetName(string name)
    {
        var clean = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray()).Trim();
        return (clean.Length == 0 ? "Planilha" : clean).Length > 31 ? clean[..31] : (clean.Length == 0 ? "Planilha" : clean);
    }

    /// <summary>Fontes, preenchimentos, bordas e formatos de número usados acima.</summary>
    private static Stylesheet Styles()
    {
        var numberFormats = new NumberingFormats(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "dd/mm/yyyy" },
            new NumberingFormat { NumberFormatId = 165, FormatCode = "dd/mm/yyyy hh:mm" }) { Count = 2 };
        var fonts = new Fonts(
            new Font(new FontSize { Val = 10 }, new Color { Rgb = "FF262626" }, new FontName { Val = "Segoe UI" }),
            new Font(new Bold(), new FontSize { Val = 10 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Segoe UI" }),
            new Font(new Bold(), new FontSize { Val = 10 }, new Color { Rgb = "FF1F3B57" }, new FontName { Val = "Segoe UI" })) { Count = 3 };
        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FF1F4E79" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFEEF4FB" }) { PatternType = PatternValues.Solid })) { Count = 4 };
        BorderPropertiesType Side<T>() where T : BorderPropertiesType, new() => new T { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FFD9DEE5" } };
        var borders = new Borders(
            new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
            new Border((LeftBorder)Side<LeftBorder>(), (RightBorder)Side<RightBorder>(), (TopBorder)Side<TopBorder>(), (BottomBorder)Side<BottomBorder>(), new DiagonalBorder())) { Count = 2 };

        CellFormat Fmt(uint numFmt, uint font, uint fill, HorizontalAlignmentValues? h, bool wrap = false) => new(
            new Alignment { Horizontal = h, Vertical = VerticalAlignmentValues.Center, WrapText = wrap })
        {
            NumberFormatId = numFmt, FontId = font, FillId = fill, BorderId = 1, FormatId = 0,
            ApplyNumberFormat = numFmt != 0, ApplyFont = true, ApplyFill = fill != 0, ApplyBorder = true, ApplyAlignment = true,
        };
        var formats = new CellFormats(
            Fmt(0, 0, 0, HorizontalAlignmentValues.Left),                       // Normal
            Fmt(0, 1, 2, HorizontalAlignmentValues.Center, wrap: true),          // Header
            Fmt(0, 2, 3, HorizontalAlignmentValues.Left),                        // Label (1ª coluna da vertical)
            Fmt(173, 0, 0, HorizontalAlignmentValues.Right),                     // Integer: até 9999 sem separador (2025), depois #,##0
            Fmt(164, 0, 0, HorizontalAlignmentValues.Right),                     // Date
            Fmt(165, 0, 0, HorizontalAlignmentValues.Right),                     // DateTime
            Fmt(0, 0, 0, HorizontalAlignmentValues.Center));                     // Sim/Não
        numberFormats.Append(new NumberingFormat { NumberFormatId = 173, FormatCode = "[>=10000]#,##0;[<=-10000]-#,##0;0" });
        // Decimais 0..6: #,##0 · #,##0.0 · #,##0.00 …
        for (uint s = 0; s <= 6; s++)
        {
            var id = 166 + s;
            numberFormats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = s == 0 ? "#,##0" : "#,##0." + new string('0', (int)s) });
            formats.Append(Fmt(id, 0, 0, HorizontalAlignmentValues.Right));
        }
        formats.Append(Fmt(0, 0, 0, HorizontalAlignmentValues.Left, wrap: true)); // Wrap (texto longo, ex.: o SQL)
        // Cópias com fundo azul-claro (faixa por registro): mesmo número, fonte e alinhamento.
        foreach (var f in formats.Elements<CellFormat>().ToList())
        {
            var copy = (CellFormat)f.CloneNode(true);
            copy.FillId = 3;
            copy.ApplyFill = true;
            formats.Append(copy);
        }
        numberFormats.Count = (uint)numberFormats.ChildElements.Count;
        formats.Count = (uint)formats.ChildElements.Count;
        return new Stylesheet(numberFormats, fonts, fills, borders,
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            formats,
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }
}
