// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NXPilot.Integrations;

/// <summary>
/// Monta o relatório de um projeto PBIP a partir de uma descrição em JSON (powerbi_build_report): medidas DAX
/// (no TMDL da tabela), relacionamentos (relationships.tmdl) e páginas com visuais (report.json no formato que o
/// Power BI Desktop abre). O agente diz O QUE quer — campos como "Tabela[coluna]" ou "[Medida]" — e o NXPilot faz
/// o COMO: confere que cada campo existe, posiciona os visuais e escreve o JSON. Era o script que o agente
/// reescrevia a cada relatório.
/// </summary>
public static partial class PowerBiReportBuilder
{
    /// <summary>Visuais aceitos e os papéis de cada um (onde os campos entram).</summary>
    public static readonly string[] VisualTypes =
        ["card", "slicer", "clusteredColumnChart", "clusteredBarChart", "lineChart", "donutChart", "pieChart", "tableEx", "lineClusteredColumnComboChart", "textbox"];

    private sealed class ModelInfo
    {
        public Dictionary<string, (string File, HashSet<string> Columns, HashSet<string> Measures)> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? TableOfMeasure(string measure) => Tables.FirstOrDefault(t => t.Value.Measures.Contains(measure)).Key;
    }

    /// <summary>Aplica a descrição ao projeto; devolve o resumo. Erro (nada gravado) se algum campo não existe.</summary>
    public static string Build(PowerBiProject pbi, JsonObject spec)
    {
        if (pbi.SemanticModelDir == null || pbi.ReportDir == null)
            throw new InvalidOperationException(NxText.T("O projeto precisa das pastas .SemanticModel e .Report.", "The project needs the .SemanticModel and .Report folders.", "El proyecto necesita las carpetas .SemanticModel y .Report."));
        var def = Path.Combine(pbi.SemanticModelDir, "definition");
        var model = ReadModel(def);
        var errors = new List<string>();
        var done = new List<string>();

        // 1) Medidas: entram no modelo antes de conferir os visuais (um visual pode usar uma medida nova).
        var measures = (spec["measures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var m in measures)
        {
            var table = Str(m, "table"); var name = Str(m, "name"); var expr = Str(m, "expression");
            if (table == null || name == null || expr == null) { errors.Add("measure: table, name e expression são obrigatórios."); continue; }
            if (!model.Tables.TryGetValue(table, out var t)) { errors.Add($"measure {name}: a tabela '{table}' não existe."); continue; }
            if (t.Columns.Contains(name)) errors.Add($"measure {name}: já existe uma coluna com esse nome em {table}.");
            else t.Measures.Add(name);
        }
        var relationships = (spec["relationships"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var r in relationships)
            foreach (var key in new[] { "from", "to" })
                if (Field(Str(r, key)) is not { Table: { } tt, Measure: false } f || !model.Tables.TryGetValue(tt, out var tinfo) || !tinfo.Columns.Contains(f.Name))
                    errors.Add($"relationship {key} '{Str(r, key)}': use Tabela[coluna] de uma coluna que existe.");

        var pages = (spec["pages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var page in pages)
            foreach (var v in (page["visuals"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var type = Str(v, "type") ?? "";
                if (!VisualTypes.Contains(type)) { errors.Add($"visual '{type}': tipo não suportado ({string.Join(", ", VisualTypes)})."); continue; }
                foreach (var f in AllFields(v))
                    if (Resolve(model, f) == null) errors.Add($"{Str(page, "name")} / {type}: o campo '{f}' não existe no modelo (use Tabela[coluna] ou [Medida]).");
            }
        if (errors.Count > 0)
            throw new InvalidOperationException(NxText.T("Nada foi gravado. Corrija: ", "Nothing was written. Fix: ", "No se grabó nada. Corrija: ") + "\n- " + string.Join("\n- ", errors.Distinct().Take(30)));

        // 2) Grava as medidas no TMDL da tabela, logo depois do lineageTag da tabela.
        foreach (var g in measures.GroupBy(m => Str(m, "table")!, StringComparer.OrdinalIgnoreCase))
        {
            var file = model.Tables[g.Key].File;
            var lines = File.ReadAllLines(file).ToList();
            var at = lines.FindIndex(l => l.StartsWith("\tlineageTag:")) + 1;
            var block = new List<string> { "" };
            foreach (var m in g)
            {
                var name = Str(m, "name")!;
                if (lines.Any(l => l.StartsWith("\tmeasure " + PowerBiDataflowModel.Name(name) + " ="))) { done.Add($"medida {name} já existia (mantida)"); continue; }
                block.Add($"\tmeasure {PowerBiDataflowModel.Name(name)} = {Regex.Replace(Str(m, "expression")!, @"\s+", " ").Trim()}");
                if (Str(m, "format") is { } fmt) block.Add($"\t\tformatString: {fmt}");
                if (Str(m, "folder") is { } folder) block.Add($"\t\tdisplayFolder: {folder}");
                block.Add($"\t\tlineageTag: {PowerBiDataflowModel.Tag($"measure:{g.Key}|{name}")}");
                block.Add("");
                done.Add($"medida {name}");
            }
            if (block.Count > 1) { lines.InsertRange(at, block); WriteLines(file, lines); }
        }

        // 3) Relacionamentos (fato → dimensão, muitos para um).
        if (relationships.Count > 0)
        {
            var relFile = Path.Combine(def, "relationships.tmdl");
            var text = File.Exists(relFile) ? File.ReadAllText(relFile) : "";
            var sb = new StringBuilder(text.Length > 0 && !text.EndsWith('\n') ? text + "\n" : text);
            foreach (var r in relationships)
            {
                var from = Field(Str(r, "from"))!.Value; var to = Field(Str(r, "to"))!.Value;
                var fromRef = $"{PowerBiDataflowModel.Name(from.Table!)}.{PowerBiDataflowModel.Name(from.Name)}";
                var toRef = $"{PowerBiDataflowModel.Name(to.Table!)}.{PowerBiDataflowModel.Name(to.Name)}";
                if (text.Contains($"fromColumn: {fromRef}\n") && text.Contains($"toColumn: {toRef}\n")) continue;
                sb.Append($"relationship {PowerBiDataflowModel.Tag($"rel:{fromRef}->{toRef}")}\n\tfromColumn: {fromRef}\n\ttoColumn: {toRef}\n\n");
                done.Add($"relacionamento {Str(r, "from")} → {Str(r, "to")}");
            }
            WriteLines(relFile, sb.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Append("").ToList());
        }

        // 4) Páginas no report.json (formato que o Power BI Desktop abre; o tema base continua).
        if (pages.Count > 0)
        {
            var (path, pbir) = PowerBiReport.ReportJson(pbi.ReportDir);
            if (path == null || pbir)
                throw new InvalidOperationException(NxText.T("powerbi_build_report monta relatórios no formato report.json (o que powerbi_from_dataflow cria); este projeto usa PBIR.",
                    "powerbi_build_report builds reports in the report.json format (what powerbi_from_dataflow creates); this project uses PBIR.",
                    "powerbi_build_report arma informes en el formato report.json (el que crea powerbi_from_dataflow); este proyecto usa PBIR."));
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var sections = root["sections"] as JsonArray ?? new JsonArray();
            // Páginas vazias (a "Página 1" do projeto novo) saem; as que já têm visuais ficam.
            foreach (var empty in sections.OfType<JsonObject>().Where(s => (s["visualContainers"] as JsonArray)?.Count is null or 0).ToList()) sections.Remove(empty);
            foreach (var page in pages)
            {
                var name = Str(page, "name") ?? $"Página {sections.Count + 1}";
                var containers = new JsonArray();
                var visuals = (page["visuals"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var placed = Layout(visuals);
                for (var i = 0; i < visuals.Count; i++) containers.Add(Container(model, visuals[i], placed[i], $"{name}|{i}"));
                sections.Add(new JsonObject
                {
                    ["config"] = "{}", ["displayName"] = name, ["displayOption"] = 1, ["filters"] = "[]", ["height"] = 720.0,
                    ["name"] = "ReportSection" + PowerBiDataflowModel.Tag("page:" + name).Replace("-", "")[..20], ["ordinal"] = sections.Count,
                    ["visualContainers"] = containers, ["width"] = 1280.0,
                });
                done.Add($"página {name} ({visuals.Count} visuais)");
            }
            var ordinal = 0;
            foreach (var s in sections.OfType<JsonObject>()) s["ordinal"] = ordinal++;
            root["sections"] = sections;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }
        return NxText.T("Feito: ", "Done: ", "Hecho: ") + string.Join("; ", done) + ".";
    }

    // ------------------------------------------------------------------ modelo

    private static ModelInfo ReadModel(string def)
    {
        var info = new ModelInfo();
        var dir = Path.Combine(def, "tables");
        if (!Directory.Exists(dir)) return info;
        foreach (var file in Directory.EnumerateFiles(dir, "*.tmdl"))
        {
            string? table = null;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var meas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith("table ")) table = Unquote(line[6..].Trim());
                else if (line.StartsWith("\tcolumn ")) cols.Add(Unquote(line[8..].Split(" =")[0].Trim()));
                else if (line.StartsWith("\tmeasure ")) meas.Add(Unquote(line[9..].Split(" =")[0].Trim()));
            }
            if (table != null) info.Tables[table] = (file, cols, meas);
        }
        return info;
    }

    private static string Unquote(string name) => name.Length > 1 && name[0] == '\'' && name[^1] == '\'' ? name[1..^1].Replace("''", "'") : name;

    /// <summary>"Tabela[coluna]" → (Tabela, coluna); "[Medida]" → (nulo, Medida, medida).</summary>
    private static (string? Table, string Name, bool Measure)? Field(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = FieldRef().Match(text.Trim());
        if (!m.Success) return null;
        var table = m.Groups[1].Value.Trim().Trim('\'');
        return (table.Length > 0 ? table : null, m.Groups[2].Value.Trim(), table.Length == 0);
    }

    /// <summary>Campo resolvido no modelo: tabela, nome e se é medida. Nulo = não existe.</summary>
    private static (string Table, string Name, bool Measure)? Resolve(ModelInfo model, string text)
    {
        if (Field(text) is not { } f) return null;
        if (f.Measure) return model.TableOfMeasure(f.Name) is { } owner ? (owner, f.Name, true) : null;
        if (!model.Tables.TryGetValue(f.Table!, out var t)) return null;
        if (t.Columns.Contains(f.Name)) return (model.Tables.Keys.First(k => k.Equals(f.Table, StringComparison.OrdinalIgnoreCase)), f.Name, false);
        if (t.Measures.Contains(f.Name)) return (f.Table!, f.Name, true); // Tabela[Medida] também vale
        return null;
    }

    private static IEnumerable<string> AllFields(JsonObject v) =>
        new[] { Str(v, "category") }.Where(s => s != null).Select(s => s!)
            .Concat(Strings(v, "values")).Concat(Strings(v, "line_values"));

    // ------------------------------------------------------------------ visuais

    private sealed record Box(double X, double Y, double W, double H);

    /// <summary>
    /// Posição de cada visual: a informada (x, y, width, height) ou automática numa grade de 1280×720 — segmentações
    /// no topo, cartões na faixa seguinte, gráficos e tabelas em duas colunas abaixo, texto acima de tudo.
    /// </summary>
    private static List<Box> Layout(List<JsonObject> visuals)
    {
        const double W = 1280, M = 16;
        var boxes = new Box?[visuals.Count];
        double y = M;
        void Row(Func<string, bool> kind, double h, int perRow)
        {
            var idx = Enumerable.Range(0, visuals.Count).Where(i => boxes[i] == null && !HasPos(visuals[i]) && kind(Str(visuals[i], "type") ?? "")).ToList();
            for (var k = 0; k < idx.Count; k += perRow)
            {
                var row = idx.Skip(k).Take(perRow).ToList();
                var w = (W - M * (row.Count + 1)) / row.Count;
                for (var j = 0; j < row.Count; j++) boxes[row[j]] = new Box(M + j * (w + M), y, w, h);
                y += h + M;
            }
        }
        Row(t => t == "textbox", 48, 1);
        Row(t => t == "slicer", 76, 4);
        Row(t => t == "card", 104, 5);
        var rest = Enumerable.Range(0, visuals.Count).Count(i => boxes[i] == null && !HasPos(visuals[i]));
        var rows = Math.Max(1, (int)Math.Ceiling(rest / 2.0));
        Row(_ => true, Math.Max(180, (720 - y - M * rows) / rows), 2);
        return visuals.Select((v, i) => HasPos(v)
            ? new Box(Num(v, "x") ?? 0, Num(v, "y") ?? 0, Num(v, "width") ?? 300, Num(v, "height") ?? 200)
            : boxes[i]!).ToList();
    }

    private static bool HasPos(JsonObject v) => Num(v, "x") != null && Num(v, "y") != null;

    private static JsonObject Container(ModelInfo model, JsonObject v, Box box, string seed)
    {
        var type = Str(v, "type")!;
        var name = PowerBiDataflowModel.Tag("visual:" + seed).Replace("-", "")[..20];
        JsonObject single;
        if (type == "textbox")
        {
            single = new JsonObject
            {
                ["visualType"] = "textbox", ["drillFilterOtherVisuals"] = true,
                ["objects"] = new JsonObject
                {
                    ["general"] = new JsonArray(new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["paragraphs"] = new JsonArray(new JsonObject
                            {
                                ["textRuns"] = new JsonArray(new JsonObject
                                {
                                    ["value"] = Str(v, "text") ?? Str(v, "title") ?? "",
                                    ["textStyle"] = new JsonObject { ["fontSize"] = "20pt", ["fontWeight"] = "bold" },
                                }),
                            }),
                        },
                    }),
                },
            };
        }
        else
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var select = new JsonArray();
            var projections = new JsonObject();
            string Alias(string table) => aliases.TryGetValue(table, out var a) ? a : aliases[table] = "t" + aliases.Count;
            void Add(string role, string field, bool active = false)
            {
                var r = Resolve(model, field)!.Value;
                var queryRef = $"{r.Table}.{r.Name}";
                var reference = new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = Alias(r.Table) } }, ["Property"] = r.Name };
                if (!select.OfType<JsonObject>().Any(s => s["Name"]?.GetValue<string>() == queryRef))
                    select.Add(new JsonObject { [r.Measure ? "Measure" : "Column"] = reference, ["Name"] = queryRef });
                var list = projections[role] as JsonArray ?? (JsonArray)(projections[role] = new JsonArray());
                var item = new JsonObject { ["queryRef"] = queryRef };
                if (active) item["active"] = true;
                list.Add(item);
            }
            var values = Strings(v, "values").ToList();
            switch (type)
            {
                case "card" or "slicer" or "tableEx":
                    foreach (var f in values) Add("Values", f, type == "slicer");
                    break;
                case "lineClusteredColumnComboChart":
                    if (Str(v, "category") is { } cc) Add("Category", cc, true);
                    foreach (var f in values) Add("Y", f);
                    foreach (var f in Strings(v, "line_values")) Add("Y2", f);
                    break;
                default: // colunas, barras, linha, rosca, pizza
                    if (Str(v, "category") is { } c) Add("Category", c, true);
                    foreach (var f in values) Add("Y", f);
                    break;
            }
            single = new JsonObject
            {
                ["visualType"] = type, ["projections"] = projections,
                ["prototypeQuery"] = new JsonObject
                {
                    ["Version"] = 2,
                    ["From"] = new JsonArray(aliases.Select(a => (JsonNode)new JsonObject { ["Name"] = a.Value, ["Entity"] = a.Key, ["Type"] = 0 }).ToArray()),
                    ["Select"] = select,
                },
                ["drillFilterOtherVisuals"] = true,
            };
            if (type == "slicer")
            {
                single["objects"] = new JsonObject { ["data"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["mode"] = Literal("'" + (Str(v, "mode") ?? "Dropdown") + "'") } }) };
                // Com título, o cabeçalho da segmentação (nome técnico da coluna) sai: fica só o título.
                if (Str(v, "title") != null)
                    single["objects"]!["header"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["show"] = Literal("false") } });
            }
            if (Str(v, "title") is { } title)
                single["vcObjects"] = new JsonObject { ["title"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["show"] = Literal("true"), ["text"] = Literal("'" + title.Replace("'", "''") + "'") } }) };
        }
        var config = new JsonObject
        {
            ["name"] = name,
            ["layouts"] = new JsonArray(new JsonObject { ["id"] = 0, ["position"] = new JsonObject { ["x"] = box.X, ["y"] = box.Y, ["z"] = 0, ["width"] = box.W, ["height"] = box.H } }),
            ["singleVisual"] = single,
        };
        return new JsonObject
        {
            ["x"] = box.X, ["y"] = box.Y, ["z"] = 0.0, ["width"] = box.W, ["height"] = box.H,
            ["config"] = config.ToJsonString(), ["filters"] = "[]",
        };
    }

    private static JsonObject Literal(string value) => new() { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    // ------------------------------------------------------------------ util

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static IEnumerable<string> Strings(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
        : Str(o, key) is { } one ? [one] : [];

    private static void WriteLines(string path, List<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines).TrimEnd('\n') + "\n", new UTF8Encoding(false));

    [GeneratedRegex(@"^('?[^\[\]']*'?)\[([^\]]+)\]$")] private static partial Regex FieldRef();
}
