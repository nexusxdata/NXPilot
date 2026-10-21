// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NXPilot.Databricks;
using NXPilot.GitRepos;
using NXPilot.Integrations;
using NXPilot.Mail;
using NXPilot.Office;
using static NXPilot.Mcp.ToolArgs;

namespace NXPilot.Mcp;

/// <summary>
/// Ferramentas do MCP NXPilot que não dependem de janela: PowerPoint e Excel (Open XML), Power BI
/// (PBIP como código), Databricks, Git, e-mail, work items e Teams. Cada extensão só aparece
/// para o agente quando está configurada no NXPilot (tokens e conexões ficam no app — o agente
/// nunca os vê). As de janela (render do PowerPoint, captura do Power BI, apps do Windows)
/// ficam no app desktop.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CoreTools
{
    public static IEnumerable<NxTool> All() =>
        PowerPoint().Concat(Excel()).Concat(PowerBi()).Concat(Databricks()).Concat(Git()).Concat(Mail()).Concat(WorkItems()).Concat(Teams());

    // ================================================================ PowerPoint

    private const string SlideSpecHelp =
        "Each slide is an object with \"layout\" and its fields: " +
        "title {title, subtitle?, footer?}; section {title, subtitle?}; bullets {title, bullets: [\"text\" | {text, level 0-3}]}; " +
        "two_columns {title, left_title?, left: [...], right_title?, right: [...]}; table {title, columns: [..], rows: [[..]]}; " +
        "image {title, image: path to PNG/JPEG in the worktree, caption?}; kpis {title, items: [{label, value, detail?, trend?: up|down|good|bad}] (max 6)}; " +
        "bars {title, unit?, items: [{label, value number}] (horizontal bar chart, max 12)}. Any slide accepts footer. Use **bold** inside texts. " +
        "Keep slides short (max ~6 bullets); split long content into more slides.";

    private static IEnumerable<NxTool> PowerPoint()
    {
        yield return new NxTool
        {
            Name = "pptx_build", Extension = "PowerPoint",
            Description = "Create a .pptx presentation from a JSON slide list (no Python, no packages). Colors and fonts come from the theme: NX theme by default, " +
                          "or the theme of a template .pptx. Overwrites the output. " + SlideSpecHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["output","slides"],"properties":{
                  "output":{"type":"string","description":"Output .pptx path (relative to the worktree)"},
                  "slides":{"type":"array","items":{"type":"object"},"description":"Slides, see the tool description"},
                  "template":{"type":"string","description":"Optional template .pptx: its theme, fonts and blank layout are used; its slides are dropped"},
                  "theme":{"type":"object","description":"Optional colors for the NX theme: {primary: hex, accent: hex, text: hex, font: name}"}}}
                """),
            Risk = (a, c) => Max(c.PathRisk(Req(a, "output"), write: true), c.PathRisk(Str(a, "template"), write: false)),
            Run = (a, c, _) =>
            {
                var output = Pptx(c.Resolve(Req(a, "output")));
                var count = PptxDocument.Build(output, Arr(a, "slides"), c.Worktree, Str(a, "template") is { } t ? c.Resolve(t) : null,
                    PptxDocument.Palette.From(a["theme"] as JsonObject));
                return Task.FromResult(Saved(c, output, $"{count} slide(s)"));
            },
        };
        yield return new NxTool
        {
            Name = "pptx_read", Extension = "PowerPoint", ReadOnly = true,
            Description = "Read a .pptx as markdown: slide by slide, each shape with its name, tables and image captions. Use it before editing an existing presentation.",
            Schema = PathSchema("The .pptx to read"),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: false),
            Run = (a, c, _) => Task.FromResult(ToolResult.Text(PptxDocument.ReadAsMarkdown(Existing(c, a)))),
        };
        yield return new NxTool
        {
            Name = "pptx_replace_text", Extension = "PowerPoint",
            Description = "Replace text in a .pptx (all slides, or one slide). Works across PowerPoint's split text runs. Returns how many replacements were made.",
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path","find","replace"],"properties":{
                  "path":{"type":"string"},"find":{"type":"string"},"replace":{"type":"string"},
                  "slide":{"type":"integer","description":"Only this slide (1 = first)"}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = Existing(c, a);
                var n = PptxDocument.ReplaceText(path, Req(a, "find"), Str(a, "replace") ?? "", Int(a, "slide"));
                return Task.FromResult(n == 0
                    ? ToolResult.Error(NxText.T("Texto não encontrado — nada mudou. Use pptx_read para ver o texto exato.", "Text not found — nothing changed. Use pptx_read to see the exact text.", "Texto no encontrado; nada cambió. Use pptx_read para ver el texto exacto."))
                    : Saved(c, path, NxText.T($"{n} troca(s)", $"{n} replacement(s)", $"{n} reemplazo(s)")));
            },
        };
        yield return new NxTool
        {
            Name = "pptx_add_slides", Extension = "PowerPoint",
            Description = "Add slides to an existing .pptx (uses its theme and blank layout). " + SlideSpecHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path","slides"],"properties":{
                  "path":{"type":"string"},"slides":{"type":"array","items":{"type":"object"}},
                  "position":{"type":"integer","description":"Insert before this slide (1 = first); omitted = at the end"}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = Existing(c, a);
                var total = PptxDocument.AddSlides(path, Arr(a, "slides"), c.Worktree, Int(a, "position"));
                return Task.FromResult(Saved(c, path, NxText.T($"agora com {total} slide(s)", $"now {total} slide(s)", $"ahora con {total} diapositiva(s)")));
            },
        };
        yield return new NxTool
        {
            Name = "pptx_delete_slides", Extension = "PowerPoint",
            Description = "Delete slides from a .pptx by number (1 = first).",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["path","slides"],"properties":{"path":{"type":"string"},"slides":{"type":"array","items":{"type":"integer"}}}}"""),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = Existing(c, a);
                var total = PptxDocument.DeleteSlides(path, Ints(a, "slides"));
                return Task.FromResult(Saved(c, path, NxText.T($"restam {total} slide(s)", $"{total} slide(s) left", $"quedan {total} diapositiva(s)")));
            },
        };
        yield return new NxTool
        {
            Name = "pptx_reorder_slides", Extension = "PowerPoint",
            Description = "Reorder the slides of a .pptx: order lists every current slide number once, in the new order (e.g. [3,1,2]).",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["path","order"],"properties":{"path":{"type":"string"},"order":{"type":"array","items":{"type":"integer"}}}}"""),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = Existing(c, a);
                PptxDocument.Reorder(path, Ints(a, "order"));
                return Task.FromResult(Saved(c, path, NxText.T("nova ordem gravada", "new order saved", "nuevo orden guardado")));
            },
        };
    }

    private static string Pptx(string path) => Path.GetExtension(path).Equals(".pptx", StringComparison.OrdinalIgnoreCase)
        ? path : throw new ArgumentException(NxText.T("O arquivo precisa ser .pptx.", "The file must be .pptx.", "El archivo debe ser .pptx."));

    private static string Existing(ToolContext c, JsonObject a)
    {
        var path = Pptx(c.Resolve(Req(a, "path")));
        return File.Exists(path) ? path : throw new FileNotFoundException(NxText.T($"Não existe: {Req(a, "path")}", $"Not found: {Req(a, "path")}", $"No existe: {Req(a, "path")}"));
    }

    /// <summary>Gravou: confere o esquema Open XML e lembra o agente de olhar o resultado.</summary>
    private static ToolResult Saved(ToolContext c, string path, string detail)
    {
        var rel = c.IsInside(path) ? Path.GetRelativePath(c.Worktree, path) : path;
        var errors = PptxDocument.Validate(path);
        var text = $"OK: {rel} ({detail}).";
        if (errors.Count > 0) text += "\nOpen XML validation warnings:\n- " + string.Join("\n- ", errors);
        text += "\nNext: pptx_render to look at the slides (if available), then fix what looks bad.";
        return ToolResult.Text(text);
    }

    private static JsonObject PathSchema(string description) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("path"),
        ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string", ["description"] = description } },
    };

    private static Risk.RiskResult Max(Risk.RiskResult a, Risk.RiskResult b) => b.Level > a.Level ? b : a;

    // ================================================================ Excel

    private const string CellValuesHelp =
        "Cell values: text, number, true/false, null (clears the value, keeps the format), \"=FORMULA\" (English function names, comma separators, e.g. \"=SUM(B2:B9)\") " +
        "or {\"date\": \"2026-10-21\"} (stored as an Excel date). Existing cell formats are kept. Formulas are not calculated here: Excel calculates on open, " +
        "and xlsx_recalculate (if available) stores the results. For bulk transformations of thousands of rows, a script is cheaper than passing every value here.";

    private static IEnumerable<NxTool> Excel()
    {
        yield return new NxTool
        {
            Name = "xlsx_info", Extension = "Excel", ReadOnly = true,
            Description = "Summary of an .xlsx/.xlsm: sheets with used range, row count, formulas and Excel tables; defined names. Call it first instead of reading everything.",
            Schema = PathSchema("The .xlsx/.xlsm to inspect"),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: false),
            Run = (a, c, _) => Task.FromResult(ToolResult.Text(XlsxDocument.Info(ExistingXlsx(c, a)))),
        };
        yield return new NxTool
        {
            Name = "xlsx_read", Extension = "Excel", ReadOnly = true,
            Description = "Read one sheet as a markdown table (columns by letter, rows by number), in a range or the used range, up to max_rows (default 100). " +
                          "Dates come as yyyy-mm-dd; formulas as value ⟨=FORMULA⟩. Read the header and a sample first; ask for more rows only if needed.",
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path"],"properties":{
                  "path":{"type":"string"},
                  "sheet":{"type":"string","description":"Sheet name; default: the first one"},
                  "range":{"type":"string","description":"e.g. A1:F50; default: the used range"},
                  "max_rows":{"type":"integer","description":"Default 100, max 1000"}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: false),
            Run = (a, c, _) => Task.FromResult(ToolResult.Text(XlsxDocument.Read(ExistingXlsx(c, a), Str(a, "sheet"), Str(a, "range"), Int(a, "max_rows") ?? 100))),
        };
        yield return new NxTool
        {
            Name = "xlsx_write_cells", Extension = "Excel",
            Description = "Write a block of cells starting at a cell: rows is a list of rows, each a list of values (left to right). " + CellValuesHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path","sheet","start","rows"],"properties":{
                  "path":{"type":"string"},"sheet":{"type":"string"},
                  "start":{"type":"string","description":"Top-left cell, e.g. B2"},
                  "rows":{"type":"array","items":{"type":"array"},"description":"[[v, v, ...], ...]"}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = ExistingXlsx(c, a);
                var at = $"{Req(a, "sheet")}!{Req(a, "start")}";
                var n = XlsxDocument.WriteCells(path, Req(a, "sheet"), Req(a, "start"), Arr(a, "rows"));
                return Task.FromResult(SavedXlsx(c, path, NxText.T($"{n} célula(s) gravada(s) em {at}", $"{n} cell(s) written at {at}", $"{n} celda(s) escrita(s) en {at}")));
            },
        };
        yield return new NxTool
        {
            Name = "xlsx_append_rows", Extension = "Excel",
            Description = "Append rows after the last used row of a sheet, copying the format of the row above. An Excel table that ends there grows to include them. " + CellValuesHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path","sheet","rows"],"properties":{
                  "path":{"type":"string"},"sheet":{"type":"string"},
                  "rows":{"type":"array","items":{"type":"array"}}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = ExistingXlsx(c, a);
                var rows = Arr(a, "rows");
                var first = XlsxDocument.AppendRows(path, Req(a, "sheet"), rows);
                return Task.FromResult(SavedXlsx(c, path, NxText.T($"{rows.Count} linha(s) a partir da {first}", $"{rows.Count} row(s) from row {first}", $"{rows.Count} fila(s) desde la {first}")));
            },
        };
        yield return new NxTool
        {
            Name = "xlsx_add_sheet", Extension = "Excel",
            Description = "Add a new sheet to an existing workbook, optionally with rows written from A1. " + CellValuesHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["path","name"],"properties":{
                  "path":{"type":"string"},"name":{"type":"string","description":"1-31 characters, unique"},
                  "rows":{"type":"array","items":{"type":"array"}}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "path"), write: true),
            Run = (a, c, _) =>
            {
                var path = ExistingXlsx(c, a);
                XlsxDocument.AddSheet(path, Req(a, "name"), a["rows"] as JsonArray);
                return Task.FromResult(SavedXlsx(c, path, NxText.T($"aba '{Req(a, "name")}' criada", $"sheet '{Req(a, "name")}' added", $"hoja '{Req(a, "name")}' creada")));
            },
        };
        yield return new NxTool
        {
            Name = "xlsx_build", Extension = "Excel",
            Description = "Create a new .xlsx (overwrites the output). sheets: [{name, rows: [[...]], header?: true, widths?: [n...]}]. " +
                          "With header (default), the first row is bold on a dark fill, frozen and filterable; column widths follow the content. " + CellValuesHelp,
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["output","sheets"],"properties":{
                  "output":{"type":"string","description":"Output .xlsx path (relative to the worktree)"},
                  "sheets":{"type":"array","items":{"type":"object"}}}}
                """),
            Risk = (a, c) => c.PathRisk(Req(a, "output"), write: true),
            Run = (a, c, _) =>
            {
                var output = c.Resolve(Req(a, "output"));
                if (!Path.GetExtension(output).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(NxText.T("O arquivo novo precisa ser .xlsx.", "The new file must be .xlsx.", "El archivo nuevo debe ser .xlsx."));
                var n = XlsxDocument.Build(output, Arr(a, "sheets"));
                return Task.FromResult(SavedXlsx(c, output, NxText.T($"{n} aba(s)", $"{n} sheet(s)", $"{n} hoja(s)")));
            },
        };
    }

    private static string ExistingXlsx(ToolContext c, JsonObject a)
    {
        var path = c.Resolve(Req(a, "path"));
        if (Path.GetExtension(path).ToLowerInvariant() is not (".xlsx" or ".xlsm"))
            throw new ArgumentException(NxText.T("O arquivo precisa ser .xlsx ou .xlsm (o .xls antigo não é suportado: salve como .xlsx).",
                "The file must be .xlsx or .xlsm (old .xls is not supported: save it as .xlsx).",
                "El archivo debe ser .xlsx o .xlsm (el .xls antiguo no es compatible: guárdelo como .xlsx)."));
        if (!File.Exists(path)) throw new FileNotFoundException(NxText.T($"Não existe: {Req(a, "path")}", $"Not found: {Req(a, "path")}", $"No existe: {Req(a, "path")}"));
        return path;
    }

    /// <summary>Gravou: confere o esquema Open XML e lembra que as fórmulas são calculadas pelo Excel.</summary>
    private static ToolResult SavedXlsx(ToolContext c, string path, string detail)
    {
        var rel = c.IsInside(path) ? Path.GetRelativePath(c.Worktree, path) : path;
        var errors = XlsxDocument.Validate(path);
        var text = $"OK: {rel} ({detail}).";
        if (errors.Count > 0) text += "\nOpen XML validation warnings:\n- " + string.Join("\n- ", errors);
        text += "\nFormulas are recalculated when Excel opens the file; call xlsx_recalculate (if available) to store the results and see formula errors.";
        return ToolResult.Text(text);
    }


    // ================================================================ Power BI (PBIP)

    private static IEnumerable<NxTool> PowerBi()
    {
        yield return new NxTool
        {
            Name = "powerbi_info", Extension = "Power BI", ReadOnly = true, Available = c => c.PowerBi != null,
            Description = "Summary of the PBIP project in the worktree: report pages (with visual count), tables, measures and relationships — with the file of each.",
            Schema = NxTool.ParseSchema("""{"type":"object","properties":{}}"""),
            Run = (_, c, _) => Task.FromResult(ToolResult.Text(PbipSummary(c.PowerBi!, c.Worktree))),
        };
        yield return new NxTool
        {
            Name = "powerbi_validate", Extension = "Power BI", ReadOnly = true, Available = c => c.PowerBi != null,
            Description = "Validate the PBIP project: every JSON (PBIR pages/visuals, .pbip, .pbir, .pbism) must parse, and TMDL files must be TAB-indented. Run after editing.",
            Schema = NxTool.ParseSchema("""{"type":"object","properties":{}}"""),
            Run = (_, c, _) =>
            {
                var problems = PbipProblems(c.PowerBi!, c.Worktree);
                return Task.FromResult(problems.Count == 0
                    ? ToolResult.Text(NxText.T("OK: JSON e TMDL do projeto válidos.", "OK: project JSON and TMDL are valid.", "OK: JSON y TMDL del proyecto válidos."))
                    : ToolResult.Error(string.Join("\n", problems.Take(50))));
            },
        };
    }

    private static IEnumerable<string> ProjectFiles(PowerBiProject pbi) =>
        new[] { pbi.SemanticModelDir, pbi.ReportDir }.Where(d => d != null && Directory.Exists(d))
            .SelectMany(d => Directory.EnumerateFiles(d!, "*", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".pbi" + Path.DirectorySeparatorChar))
            .Append(pbi.PbipPath);

    internal static List<string> PbipProblems(PowerBiProject pbi, string worktree)
    {
        var problems = new List<string>();
        foreach (var file in ProjectFiles(pbi))
        {
            var rel = Path.GetRelativePath(worktree, file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".json" or ".pbip" or ".pbir" or ".pbism")
            {
                try { using var _ = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                catch (JsonException ex) { problems.Add($"{rel}:{ex.LineNumber + 1}: JSON inválido — {ex.Message}"); }
            }
            else if (ext == ".tmdl")
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].StartsWith("  ") && lines[i].Trim().Length > 0 && !InsideExpression(lines, i))
                    {
                        problems.Add($"{rel}:{i + 1}: TMDL indentado com espaços — use TAB.");
                        break;
                    }
            }
        }
        return problems;
    }

    /// <summary>Expressões DAX/M em várias linhas (depois de "= ```" ou de uma linha que termina em "=") podem ter espaços.</summary>
    private static bool InsideExpression(string[] lines, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            var t = lines[j].TrimEnd();
            if (t.EndsWith('=') || t.EndsWith("```")) return true;
            if (!t.StartsWith('\t') && !t.StartsWith(' ') && t.Length > 0) return false;
        }
        return false;
    }

    internal static string PbipSummary(PowerBiProject pbi, string worktree)
    {
        var sb = new StringBuilder($"# {pbi.Name} ({Path.GetRelativePath(worktree, pbi.PbipPath)})\n");
        if (pbi.ReportDir is { } report && Directory.Exists(Path.Combine(report, "definition", "pages")))
        {
            sb.AppendLine("\n## Pages");
            foreach (var page in Directory.EnumerateFiles(Path.Combine(report, "definition", "pages"), "page.json", SearchOption.AllDirectories))
            {
                string name;
                try { name = JsonNode.Parse(File.ReadAllText(page))?["displayName"]?.GetValue<string>() ?? "?"; } catch { name = "(JSON inválido)"; }
                var visuals = Directory.Exists(Path.Combine(Path.GetDirectoryName(page)!, "visuals"))
                    ? Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(page)!, "visuals"), "visual.json", SearchOption.AllDirectories).Count() : 0;
                sb.AppendLine($"- {name} — {visuals} visual(s) — {Path.GetRelativePath(worktree, page)}");
            }
        }
        if (pbi.SemanticModelDir is { } model && Directory.Exists(Path.Combine(model, "definition")))
        {
            var tables = Path.Combine(model, "definition", "tables");
            sb.AppendLine("\n## Tables and measures");
            if (Directory.Exists(tables))
                foreach (var file in Directory.EnumerateFiles(tables, "*.tmdl"))
                {
                    var text = File.ReadAllText(file);
                    var measures = Regex.Matches(text, @"^\s*measure\s+('[^']+'|[^\s=]+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim('\'')).ToList();
                    var columns = Regex.Matches(text, @"^\s*column\s+", RegexOptions.Multiline).Count;
                    sb.AppendLine($"- {Path.GetFileNameWithoutExtension(file)} — {columns} column(s){(measures.Count > 0 ? ", measures: " + string.Join(", ", measures) : "")} — {Path.GetRelativePath(worktree, file)}");
                }
            var rel = Path.Combine(model, "definition", "relationships.tmdl");
            if (File.Exists(rel)) sb.AppendLine($"\nRelationships: {Regex.Matches(File.ReadAllText(rel), @"^relationship\s", RegexOptions.Multiline).Count} — {Path.GetRelativePath(worktree, rel)}");
        }
        return sb.ToString();
    }

    // ================================================================ Databricks

    private static IEnumerable<NxTool> Databricks()
    {
        static bool Configured(ToolContext _) => DatabricksSettings.LoadConnection() != null;
        static DatabricksConnection Conn() => DatabricksSettings.LoadConnection() ?? throw new InvalidOperationException("Databricks não configurado no NXPilot (Extensão MCP).");

        yield return new NxTool
        {
            Name = "databricks_list", Extension = "Databricks", ReadOnly = true, Available = Configured,
            Description = "List Databricks Unity Catalog objects: no args = catalogs; catalog = its schemas; catalog + schema = its tables.",
            Schema = NxTool.ParseSchema("""{"type":"object","properties":{"catalog":{"type":"string"},"schema":{"type":"string"}}}"""),
            Run = async (a, _, ct) =>
            {
                using var svc = new DatabricksSqlService(Conn());
                var (catalog, schema) = (Str(a, "catalog"), Str(a, "schema"));
                var items = catalog == null ? await svc.ListCatalogsAsync(ct)
                    : schema == null ? await svc.ListSchemasAsync(catalog, ct)
                    : await svc.ListTablesAsync(catalog, schema, ct);
                return ToolResult.Text(items.Count == 0 ? "(vazio)" : string.Join("\n", items));
            },
        };
        yield return new NxTool
        {
            Name = "databricks_describe_table", Extension = "Databricks", ReadOnly = true, Available = Configured,
            Description = "Columns and types of a Databricks table.",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["catalog","schema","table"],"properties":{"catalog":{"type":"string"},"schema":{"type":"string"},"table":{"type":"string"}}}"""),
            Run = async (a, _, ct) =>
            {
                using var svc = new DatabricksSqlService(Conn());
                var cols = await svc.DescribeTableAsync(Req(a, "catalog"), Req(a, "schema"), Req(a, "table"), ct);
                return ToolResult.Text(string.Join("\n", cols.Select(c => $"{c.Name}: {c.TypeText ?? c.TypeName}")));
            },
        };
        yield return new NxTool
        {
            Name = "databricks_sql", Extension = "Databricks", ReadOnly = true, Available = Configured,
            Description = "Run ONE SQL statement on the configured Databricks SQL warehouse. Reads run directly; writes, DDL and permission changes go to the user for approval " +
                          "(and are refused during planning/review). Returns a markdown table.",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["sql"],"properties":{"sql":{"type":"string"},"max_rows":{"type":"integer","description":"Default 100, max 1000"}}}"""),
            // O gate fica DENTRO: o classificador de SQL decide por instrução (leitura BAIXO, escrita ALTO).
            Run = async (a, c, ct) =>
            {
                ISqlStatementApprover approver = c.ReadOnlyPhase
                    ? new ReadOnlySqlApprover()
                    : new GateSqlApprover((review, risk, t) => c.Approve($"Databricks SQL: {review.Kind} — {Trim(Req(a, "sql"), 300)}", risk, t));
                using var svc = new DatabricksSqlService(Conn(), approver);
                var max = Math.Clamp(Int(a, "max_rows") ?? 100, 1, 1000);
                try
                {
                    var r = await svc.ExecuteAsync(Req(a, "sql"), null, new SqlExecutionOptions { MaxRows = max }, ct);
                    return ToolResult.Text(Markdown(r, max));
                }
                catch (SqlStatementDeniedException ex) { return ToolResult.Error(NxText.T("NEGADO no NXPilot: ", "DENIED in NXPilot: ", "DENEGADO en NXPilot: ") + ex.Message); }
            },
        };
    }

    private static string Markdown(SqlQueryResult r, int max)
    {
        if (r.Columns.Count == 0) return $"OK ({r.Kind}, {r.Elapsed.TotalSeconds:0.0} s).";
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", r.Columns.Select(c => c.Name)) + " |");
        sb.AppendLine("|" + string.Concat(r.Columns.Select(_ => " --- |")));
        foreach (var row in r.Rows.Take(max))
            sb.AppendLine("| " + string.Join(" | ", row.Select(v => (v ?? "NULL").Replace("|", "\\|").Replace("\n", " "))) + " |");
        sb.Append($"\n{r.Rows.Count} de {r.TotalRowCount} linha(s){(r.Truncated ? " — cortado" : "")} · {r.Elapsed.TotalSeconds:0.0} s");
        return sb.ToString();
    }

    // ================================================================ Git (GitHub / Azure DevOps)

    private static IEnumerable<NxTool> Git()
    {
        static bool HasToken(ToolContext _) { var s = GitRepoSettings.Load(); return s.GitHubToken.Length > 0 || s.AzureDevOpsToken.Length > 0; }
        static GitRepoService Service(ToolContext c, CancellationToken _) => new(GitRepoSettings.Load(),
            new GateGitApprover((review, risk, t) => c.Approve("Git: " + review.Summary, risk, t)));

        yield return new NxTool
        {
            Name = "git_list_repositories", Extension = "Git", ReadOnly = true, Available = HasToken,
            Description = "List the repositories the user can access on GitHub or Azure DevOps (tokens stay in NXPilot).",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["provider"],"properties":{"provider":{"type":"string","enum":["github","azuredevops"]}}}"""),
            Run = async (a, c, ct) =>
            {
                var s = GitRepoSettings.Load();
                var provider = Req(a, "provider").Contains("azure", StringComparison.OrdinalIgnoreCase) ? GitProvider.AzureDevOps : GitProvider.GitHub;
                var repos = await Service(c, ct).ListRepositoriesAsync(provider, s.AzureDevOpsOrganizationUrl, ct);
                return ToolResult.Text(string.Join("\n", repos.Select(r => $"{r} — {r.CloneUrl}")));
            },
        };
        yield return new NxTool
        {
            Name = "git_push", Extension = "Git", Available = HasToken,
            Description = "Push the task branch (current branch of the worktree) to origin. Never forces. Goes to the user for approval (main branch = HIGH risk).",
            Schema = NxTool.ParseSchema("""{"type":"object","properties":{}}"""),
            Run = async (_, c, ct) =>
            {
                try { await Service(c, ct).PushAsync(c.Worktree, null, ct); }
                catch (GitOperationDeniedException ex) { return ToolResult.Error(NxText.T("NEGADO no NXPilot: ", "DENIED in NXPilot: ", "DENEGADO en NXPilot: ") + ex.Message); }
                return ToolResult.Text("OK: push feito.");
            },
        };
        yield return new NxTool
        {
            Name = "git_create_pull_request", Extension = "Git", Available = HasToken,
            Description = "Open a pull request from the task branch on GitHub or Azure DevOps (push first). Goes to the user for approval.",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["title","description"],"properties":{"title":{"type":"string"},"description":{"type":"string"},"target_branch":{"type":"string"}}}"""),
            Run = async (a, c, ct) =>
            {
                try
                {
                    var pr = await Service(c, ct).CreatePullRequestAsync(c.Worktree, Req(a, "title"), Str(a, "description") ?? "", Str(a, "target_branch"), ct);
                    return ToolResult.Text($"PR {pr.Id}: {pr.SourceBranch} → {pr.TargetBranch} — {pr.Url}");
                }
                catch (GitOperationDeniedException ex) { return ToolResult.Error(NxText.T("NEGADO no NXPilot: ", "DENIED in NXPilot: ", "DENEGADO en NXPilot: ") + ex.Message); }
            },
        };
    }

    // ================================================================ E-mail (Outlook / Gmail) — só leitura

    private static IEnumerable<NxTool> Mail()
    {
        static List<MailProvider> LoggedIn()
        {
            var s = MailSettings.Load();
            return Enum.GetValues<MailProvider>()
                .Where(p => s.Config(p) != null && new DpapiTokenStore(p).LoadAsync().GetAwaiter().GetResult() != null).ToList();
        }
        static MailService Service(JsonObject a)
        {
            var logged = LoggedIn();
            var wanted = Str(a, "provider") is { } p && Enum.TryParse<MailProvider>(p, true, out var parsed) ? parsed : logged.FirstOrDefault();
            if (!logged.Contains(wanted)) throw new InvalidOperationException(NxText.T($"{wanted} sem login no NXPilot (Extensão MCP → E-mail).", $"{wanted} not signed in in NXPilot (MCP Extension → E-mail).", $"{wanted} sin inicio de sesión en NXPilot (Extensión MCP → Correo)."));
            return MailService.Create(wanted, MailSettings.Load().Tokens(wanted)!);
        }

        yield return new NxTool
        {
            Name = "mail_list", Extension = "E-mail", ReadOnly = true, Available = _ => LoggedIn().Count > 0,
            Description = "List the user's inbox (Outlook or Gmail, read-only): id, from, subject, date. Use mail_read for a body.",
            Schema = NxTool.ParseSchema("""
                {"type":"object","properties":{"provider":{"type":"string","enum":["Outlook","Gmail"]},"unread_only":{"type":"boolean"},
                 "search":{"type":"string"},"max":{"type":"integer","description":"Default 20, max 50"}}}
                """),
            Run = async (a, _, ct) =>
            {
                using var mail = Service(a);
                var list = await mail.ListInboxAsync(new MailQuery { UnreadOnly = Bool(a, "unread_only"), Search = Str(a, "search"), Max = Math.Clamp(Int(a, "max") ?? 20, 1, 50) }, ct);
                return ToolResult.Text(list.Count == 0 ? "(nenhum e-mail)" : string.Join("\n", list.Select(m => $"{m.Id} · {m.From} · {m.Subject}")));
            },
        };
        yield return new NxTool
        {
            Name = "mail_read", Extension = "E-mail", ReadOnly = true, Available = _ => LoggedIn().Count > 0,
            Description = "Read one e-mail (text body and attachment list). The user approves each one, since its content goes to the agent.",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["id"],"properties":{"id":{"type":"string"},"provider":{"type":"string","enum":["Outlook","Gmail"]}}}"""),
            Risk = (_, _) => NxTool.Medium(NxText.T("o conteúdo do e-mail vai para o agente", "the e-mail content goes to the agent", "el contenido del correo va al agente")),
            Run = async (a, _, ct) =>
            {
                using var mail = Service(a);
                return ToolResult.Text(await mail.GetForAgentAsync(Req(a, "id"), 8000, ct));
            },
        };
    }

    // ================================================================ Work items (Azure DevOps / GitHub Projects)

    private static IEnumerable<NxTool> WorkItems()
    {
        yield return new NxTool
        {
            Name = "workitems_mine", Extension = "Work items", ReadOnly = true,
            Available = _ => WorkItemSettings.Load() is var s && (s.DevOps() != null || s.GitHub() != null),
            Description = "The user's open work items (Azure DevOps) or project items (GitHub Projects): id, type, title, state and acceptance criteria.",
            Schema = NxTool.ParseSchema("""{"type":"object","properties":{"source":{"type":"string","enum":["devops","github"]},"top":{"type":"integer","description":"Default 30"}}}"""),
            Run = async (a, _, ct) =>
            {
                var s = WorkItemSettings.Load();
                var github = Str(a, "source")?.Contains("git", StringComparison.OrdinalIgnoreCase) ?? s.DevOps() == null;
                IReadOnlyList<WorkItem> items;
                if (github)
                {
                    using var gh = new GitHubProjectsClient((s.GitHub() ?? throw new InvalidOperationException("GitHub Projects não configurado.")).Connection);
                    items = await gh.MyOpenItemsAsync(0, 5, ct);
                }
                else
                {
                    using var ado = new AzureDevOpsClient((s.DevOps() ?? throw new InvalidOperationException("Azure DevOps não configurado.")).Connection);
                    items = await ado.MyOpenWorkItemsAsync(Math.Clamp(Int(a, "top") ?? 30, 1, 200), null, ct);
                }
                return ToolResult.Text(items.Count == 0 ? "(nenhum item aberto)" : string.Join("\n\n", items.Select(w =>
                    $"#{w.Id} [{w.Type}] {w.Title} — {w.State}\n{w.Url}" + (w.AcceptanceCriteria.Length > 0 ? $"\nCritérios: {Trim(w.AcceptanceCriteria, 600)}" : ""))));
            },
        };
        yield return new NxTool
        {
            Name = "workitems_tree", Extension = "Work items", ReadOnly = true,
            Available = _ => WorkItemSettings.Load().DevOps() != null,
            Description = "Read an Azure DevOps work item (Work Item Project, Epic, Feature, Story, Task…) and everything below it in the hierarchy, in tree order: " +
                          "level, id, type, title, state, assigned to, remaining/original hours and iteration; the root comes with description and acceptance criteria. " +
                          "Use it for the work item linked to the task (e.g. to list pending tasks). open_only=true drops closed items.",
            Schema = NxTool.ParseSchema("""
                {"type":"object","required":["id"],"properties":{
                  "id":{"type":"integer","description":"Work item id"},
                  "project":{"type":"string","description":"Azure DevOps team project; default: the one linked to the task / last used"},
                  "open_only":{"type":"boolean","description":"Only items not closed/done/removed (the root always comes)"},
                  "max":{"type":"integer","description":"Default 300"}}}
                """),
            Run = async (a, _, ct) =>
            {
                var s = WorkItemSettings.Load();
                var conn = (s.DevOps() ?? throw new InvalidOperationException("Azure DevOps não configurado.")).Connection;
                var project = Str(a, "project") is { Length: > 0 } p ? p : s.LastDevOpsProject is { Length: > 0 } last ? last : conn.Project;
                using var ado = new AzureDevOpsClient(conn with { Project = project });
                var id = Int(a, "id") ?? throw new ArgumentException("id");
                var nodes = await ado.TreeAsync(id, Bool(a, "open_only"), Math.Clamp(Int(a, "max") ?? 300, 1, 1000), ct);
                return ToolResult.Text(nodes.Count == 0
                    ? NxText.T($"Work item #{id} não encontrado no projeto {project} (ou sem acesso).", $"Work item #{id} not found in project {project} (or no access).", $"Work item #{id} no encontrado en el proyecto {project} (o sin acceso).")
                    : WorkItemTreeText(nodes, project));
            },
        };
    }

    /// <summary>Raiz com descrição/critérios; depois uma tabela na ordem da árvore (título recuado pelo nível) e o total por estado.</summary>
    internal static string WorkItemTreeText(IReadOnlyList<WorkItemNode> nodes, string project)
    {
        var root = nodes[0];
        var sb = new StringBuilder($"# {root.Type} #{root.Id} · {root.Title}\nProject: {project} · State: {root.State} · {root.Url}\n");
        if (root.Description.Length > 0) sb.Append("\nDescription:\n").Append(Trim(root.Description, 2000)).Append('\n');
        if (root.AcceptanceCriteria.Length > 0) sb.Append("\nAcceptance criteria:\n").Append(Trim(root.AcceptanceCriteria, 1500)).Append('\n');
        sb.Append("\n| Level | Id | Type | Title | State | Assigned to | Remaining h | Original h | Iteration |\n|---|---|---|---|---|---|---|---|---|\n");
        static string H(double? h) => h is { } v ? v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "";
        foreach (var n in nodes)
            sb.Append($"| {n.Depth} | {n.Id} | {n.Type} | {new string('·', n.Depth * 2)}{n.Title.Replace("|", "\\|")} | {n.State} | {n.AssignedTo} | {H(n.RemainingWork)} | {H(n.OriginalEstimate)} | {n.Iteration} |\n");
        sb.Append("\nBy state: ").Append(string.Join(", ", nodes.Skip(1).GroupBy(n => n.State).Select(g => $"{g.Key} {g.Count()}")));
        sb.Append(" · By type: ").Append(string.Join(", ", nodes.Skip(1).GroupBy(n => n.Type).Select(g => $"{g.Key} {g.Count()}")));
        return sb.ToString();
    }

    // ================================================================ Teams

    private static IEnumerable<NxTool> Teams()
    {
        yield return new NxTool
        {
            Name = "teams_send", Extension = "Teams", Available = _ => TeamsSettings.Load().IsConfigured,
            Description = "Post a message to the Teams channel configured in NXPilot. The user approves each message (it leaves the machine).",
            Schema = NxTool.ParseSchema("""{"type":"object","required":["title","text"],"properties":{"title":{"type":"string"},"text":{"type":"string"}}}"""),
            Risk = (_, _) => NxTool.Medium(NxText.T("mensagem sai para o Teams", "message goes out to Teams", "el mensaje sale a Teams")),
            Run = async (a, _, ct) =>
            {
                using var teams = new TeamsWebhookClient(TeamsSettings.Load().WebhookUrl);
                await teams.SendAsync(new TeamsMessage(Req(a, "title"), Req(a, "text")), ct);
                return ToolResult.Text("OK: enviado ao Teams.");
            },
        };
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
