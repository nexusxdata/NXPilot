// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NXPilot.Integrations;

/// <summary>
/// PBI de exemplo citado na tarefa: lê o estilo de uma aba (tamanho, fundo, tema, cada visual com
/// tipo, posição e formatação) para o agente reproduzir no PBI novo, e copia o tema. Aceita .pbip,
/// a pasta do projeto ou .pbix (o .pbix é um zip; o layout fica em Report/Layout, em UTF-16).
/// Só lê o exemplo — nunca grava nele.
/// </summary>
public static class PowerBiStyle
{
    /// <summary>Arquivos do relatório de exemplo, venham de uma pasta ou de dentro de um .pbix.</summary>
    private sealed class Source : IDisposable
    {
        private readonly string? _dir;
        private readonly ZipArchive? _zip;
        public bool Pbir { get; }
        public string Label { get; }

        public Source(string path)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full) && full.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
            {
                _zip = ZipFile.OpenRead(full);
                Label = ".pbix";
                return;
            }
            var root = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
            if (!Directory.Exists(root)) throw new FileNotFoundException("PBI de exemplo não encontrado: " + path);
            string? report = null;
            if (full.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase))
                report = Path.Combine(root, Path.GetFileNameWithoutExtension(full) + ".Report");
            else if (full.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
                report = full;
            else if (PowerBiProject.Detect(root)?.ReportDir is { } detected)
                report = detected;
            else
                report = Directory.EnumerateDirectories(root, "*.Report", SearchOption.AllDirectories).FirstOrDefault();
            if (report == null || !Directory.Exists(report)) throw new FileNotFoundException("Não achei a pasta .Report do PBI de exemplo em " + path);
            _dir = report;
            Pbir = File.Exists(Path.Combine(report, "definition", "report.json"));
            Label = Pbir ? "PBIP (PBIR)" : "PBIP (report.json)";
        }

        /// <summary>Texto de um arquivo do relatório (caminho relativo à pasta .Report, ou a Report/ no .pbix).</summary>
        public string? Read(string rel)
        {
            if (_zip != null)
            {
                var entry = _zip.GetEntry("Report/" + rel.Replace('\\', '/'));
                if (entry == null) return null;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                // Layout do .pbix vem em UTF-16 LE; os demais em UTF-8.
                return bytes.Length > 1 && bytes[1] == 0 ? Encoding.Unicode.GetString(bytes).TrimStart('﻿') : Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            }
            var file = Path.Combine(_dir!, rel);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        public byte[]? ReadBytes(string rel)
        {
            if (_zip != null)
            {
                var entry = _zip.GetEntry("Report/" + rel.Replace('\\', '/'));
                if (entry == null) return null;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            var file = Path.Combine(_dir!, rel);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }

        /// <summary>Pastas de página do PBIR (definition/pages/&lt;id&gt;).</summary>
        public IEnumerable<string> PbirPages() => _dir == null ? [] :
            Directory.Exists(Path.Combine(_dir, "definition", "pages"))
                ? Directory.EnumerateDirectories(Path.Combine(_dir, "definition", "pages")).Select(d => Path.GetRelativePath(_dir, d))
                : [];

        public IEnumerable<string> PbirVisuals(string pageRel) => _dir == null ? [] :
            Directory.Exists(Path.Combine(_dir, pageRel, "visuals"))
                ? Directory.EnumerateFiles(Path.Combine(_dir, pageRel, "visuals"), "visual.json", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dir, f))
                : [];

        /// <summary>report.json do formato antigo (ou Layout do .pbix).</summary>
        public JsonObject? Legacy() => JsonNode.Parse(Read(_zip != null ? "Layout" : "report.json") ?? "null") as JsonObject;

        public void Dispose() => _zip?.Dispose();
    }

    private sealed record Theme(string? BaseName, string? CustomName, string? CustomFile);

    private sealed record Visual(string Type, double X, double Y, double W, double H, string Title, JsonNode? Objects, JsonNode? ContainerObjects, IReadOnlyList<string> Fields);

    private sealed record Page(string Name, string DisplayName, double Width, double Height, JsonNode? Objects, IReadOnlyList<Visual> Visuals);

    /// <summary>
    /// Resumo do estilo para o agente: abas do exemplo e, da aba <paramref name="pageName"/> (nome
    /// exibido; vazio = a primeira), tamanho, fundo, tema e os visuais com posição e formatação.
    /// </summary>
    public static string Describe(string path, string? pageName = null, int maxChars = 24_000)
    {
        using var src = new Source(path);
        var (pages, theme) = src.Pbir ? ReadPbir(src) : ReadLegacy(src);
        if (pages.Count == 0) return "O PBI de exemplo não tem páginas.";
        var page = string.IsNullOrWhiteSpace(pageName) ? pages[0]
            : pages.FirstOrDefault(p => string.Equals(p.DisplayName.Trim(), pageName.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? pages.FirstOrDefault(p => p.DisplayName.Contains(pageName.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"Aba \"{pageName}\" não existe no exemplo. Abas: {string.Join(", ", pages.Select(p => p.DisplayName))}");

        var sb = new StringBuilder();
        sb.AppendLine($"# PBI de exemplo ({src.Label}) — aba \"{page.DisplayName}\"");
        sb.AppendLine("Abas: " + string.Join(" · ", pages.Select(p => $"{p.DisplayName} ({p.Visuals.Count} visuais)")));
        sb.AppendLine($"\n## Tema\nbase: {theme.BaseName ?? "(nenhum)"} · personalizado: {theme.CustomName ?? "(nenhum)"}" +
                      (theme.CustomName != null ? " — copie com powerbi_copy_theme" : ""));
        if (theme.CustomFile != null && src.Read(theme.CustomFile) is { } customJson)
            sb.AppendLine("Tema personalizado (resumo): " + Compact(ThemeEssentials(customJson), 1500));
        sb.AppendLine($"\n## Página\ntamanho: {page.Width:0} × {page.Height:0}");
        if (page.Objects != null) sb.AppendLine("formatação da página (fundo, papel de parede…): " + Compact(page.Objects, 1500));
        sb.AppendLine($"\n## Visuais ({page.Visuals.Count}), de cima para baixo");
        foreach (var v in page.Visuals.OrderBy(v => v.Y).ThenBy(v => v.X))
        {
            sb.AppendLine($"- {v.Type} · x={v.X:0} y={v.Y:0} w={v.W:0} h={v.H:0}" + (v.Title.Length > 0 ? $" · título \"{v.Title}\"" : "") +
                          (v.Fields.Count > 0 ? " · campos: " + string.Join(", ", v.Fields.Take(6)) : ""));
            if (v.Objects != null) sb.AppendLine("  formato: " + Compact(v.Objects, 700));
            if (v.ContainerObjects != null) sb.AppendLine("  contêiner (título, borda, fundo, sombra): " + Compact(v.ContainerObjects, 700));
            if (sb.Length > maxChars) { sb.AppendLine("… (cortado — peça outra aba ou menos detalhes)"); break; }
        }
        sb.AppendLine("\nReproduza no PBI novo: o mesmo tamanho de página e fundo, o tema (powerbi_copy_theme), a mesma grade de posições e os mesmos formatos de título/fonte/cor — com os campos do modelo novo.");
        return sb.ToString();
    }

    private static (List<Page>, Theme) ReadLegacy(Source src)
    {
        var root = src.Legacy() ?? throw new InvalidOperationException("Layout do relatório de exemplo ilegível.");
        var config = ParseString(root["config"]);
        var theme = LegacyTheme(config, root);
        var pages = new List<Page>();
        foreach (var s in root["sections"] as JsonArray ?? [])
        {
            if (s is not JsonObject section) continue;
            var visuals = new List<Visual>();
            foreach (var vc in section["visualContainers"] as JsonArray ?? [])
            {
                var vcfg = ParseString(vc?["config"]);
                var single = vcfg?["singleVisual"];
                if (single == null) continue;
                visuals.Add(new Visual(single["visualType"]?.GetValue<string>() ?? "?",
                    Num(vc?["x"]), Num(vc?["y"]), Num(vc?["width"]), Num(vc?["height"]),
                    TitleOf(single["vcObjects"]), single["objects"], single["vcObjects"], FieldsOf(single["projections"])));
            }
            pages.Add(new Page(section["name"]?.GetValue<string>() ?? "", section["displayName"]?.GetValue<string>() ?? "",
                Num(section["width"]), Num(section["height"]), ParseString(section["config"])?["objects"], visuals));
        }
        return (pages, theme);
    }

    private static (List<Page>, Theme) ReadPbir(Source src)
    {
        var report = JsonNode.Parse(src.Read(Path.Combine("definition", "report.json")) ?? "{}") as JsonObject ?? new JsonObject();
        var tc = report["themeCollection"];
        string? customName = tc?["customTheme"]?["name"]?.GetValue<string>();
        string? customFile = null;
        foreach (var pkg in report["resourcePackages"] as JsonArray ?? [])
            foreach (var item in pkg?["items"] as JsonArray ?? [])
                if (item?["type"]?.GetValue<string>() == "CustomTheme")
                    customFile = Path.Combine("StaticResources", "RegisteredResources", item["path"]?.GetValue<string>() ?? "");
        var theme = new Theme(tc?["baseTheme"]?["name"]?.GetValue<string>(), customName, customFile);

        var order = (JsonNode.Parse(src.Read(Path.Combine("definition", "pages", "pages.json")) ?? "{}")?["pageOrder"] as JsonArray)?
            .Select(n => n?.GetValue<string>()).ToList() ?? [];
        var pages = new List<Page>();
        foreach (var dir in src.PbirPages().OrderBy(d => { var i = order.IndexOf(Path.GetFileName(d)); return i < 0 ? int.MaxValue : i; }))
        {
            if (JsonNode.Parse(src.Read(Path.Combine(dir, "page.json")) ?? "null") is not JsonObject page) continue;
            var visuals = new List<Visual>();
            foreach (var file in src.PbirVisuals(dir))
            {
                if (JsonNode.Parse(src.Read(file) ?? "null") is not JsonObject v) continue;
                var pos = v["position"];
                var visual = v["visual"];
                visuals.Add(new Visual(visual?["visualType"]?.GetValue<string>() ?? (v["visualGroup"] != null ? "group" : "?"),
                    Num(pos?["x"]), Num(pos?["y"]), Num(pos?["width"]), Num(pos?["height"]),
                    TitleOf(visual?["visualContainerObjects"]), visual?["objects"], visual?["visualContainerObjects"], FieldsOf(visual?["query"])));
            }
            pages.Add(new Page(page["name"]?.GetValue<string>() ?? "", page["displayName"]?.GetValue<string>() ?? "",
                Num(page["width"]), Num(page["height"]), page["objects"], visuals));
        }
        return (pages, theme);
    }

    private static Theme LegacyTheme(JsonNode? config, JsonObject root)
    {
        var tc = config?["themeCollection"];
        string? customFile = null;
        foreach (var p in root["resourcePackages"] as JsonArray ?? [])
        {
            var pkg = p?["resourcePackage"];
            if (pkg?["name"]?.GetValue<string>() != "RegisteredResources") continue;
            if ((pkg["items"] as JsonArray ?? []).FirstOrDefault(i => i?["type"]?.GetValue<int>() == 201) is { } item)
                customFile = Path.Combine("StaticResources", "RegisteredResources", item["path"]?.GetValue<string>() ?? "");
        }
        return new Theme(tc?["baseTheme"]?["name"]?.GetValue<string>(), tc?["customTheme"]?["name"]?.GetValue<string>(), customFile);
    }

    /// <summary>
    /// Copia o tema do exemplo (base e personalizado) para o relatório <paramref name="targetReportDir"/>.
    /// Sem tema base no exemplo, usa <paramref name="fallbackBaseTheme"/> (o da instalação do Power BI).
    /// </summary>
    public static string CopyTheme(string examplePath, string targetReportDir, string? fallbackBaseTheme)
    {
        using var src = new Source(examplePath);
        Theme theme;
        if (src.Pbir)
            theme = ReadPbir(src).Item2;
        else
        {
            var root = src.Legacy() ?? throw new InvalidOperationException("Layout do relatório de exemplo ilegível.");
            theme = LegacyTheme(ParseString(root["config"]), root);
        }
        var done = new List<string>();

        // Tema base: o do exemplo, se o arquivo veio junto; senão o da instalação.
        var baseFile = theme.BaseName != null ? src.ReadBytes(Path.Combine("StaticResources", "SharedResources", "BaseThemes", theme.BaseName + ".json")) : null;
        string? baseTemp = null;
        if (baseFile != null)
        {
            baseTemp = Path.Combine(Path.GetTempPath(), "nxpilot-theme-" + Guid.NewGuid().ToString("N")[..8], theme.BaseName + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(baseTemp)!);
            File.WriteAllBytes(baseTemp, baseFile);
        }
        var baseToUse = baseTemp ?? fallbackBaseTheme;
        if (baseToUse != null) done.Add(PowerBiReport.Fix(targetReportDir, baseToUse));
        if (baseTemp != null) try { Directory.Delete(Path.GetDirectoryName(baseTemp)!, true); } catch { }

        // Recursos registrados do exemplo: tema personalizado e imagens (fundo da página, logos…),
        // com os mesmos nomes — assim a formatação copiada do exemplo aponta para eles.
        var items = RegisteredItems(src);
        var copied = new List<(string Name, string Path, bool IsTheme)>();
        foreach (var item in items)
        {
            if (src.ReadBytes(Path.Combine("StaticResources", "RegisteredResources", item.Path)) is not { } bytes) continue;
            var dest = Path.Combine(targetReportDir, "StaticResources", "RegisteredResources", item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            copied.Add(item);
        }
        if (copied.Count > 0) RegisterResources(targetReportDir, copied, theme.CustomName);
        done.Add(theme.CustomName != null && copied.Any(c => c.IsTheme)
            ? $"Tema personalizado \"{theme.CustomName}\" copiado."
            : "O exemplo não tem tema personalizado (só o base).");
        var images = copied.Where(c => !c.IsTheme).Select(c => c.Name).ToList();
        if (images.Count > 0)
            done.Add($"Imagens copiadas e registradas com o mesmo nome ({string.Join(", ", images)}): use-as na formatação copiada do exemplo (ex.: fundo da página).");
        return string.Join(" ", done);
    }

    /// <summary>Itens do pacote RegisteredResources do exemplo (tema personalizado e imagens).</summary>
    private static List<(string Name, string Path, bool IsTheme)> RegisteredItems(Source src)
    {
        var list = new List<(string, string, bool)>();
        JsonArray? packages;
        if (src.Pbir) packages = (JsonNode.Parse(src.Read(Path.Combine("definition", "report.json")) ?? "{}"))?["resourcePackages"] as JsonArray;
        else packages = src.Legacy()?["resourcePackages"] as JsonArray;
        foreach (var p in packages ?? [])
        {
            var pkg = src.Pbir ? p : p?["resourcePackage"];
            if (pkg?["name"]?.GetValue<string>() != "RegisteredResources") continue;
            foreach (var i in pkg["items"] as JsonArray ?? [])
            {
                var path = i?["path"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path)) continue;
                var type = i!["type"];
                var isTheme = type is JsonValue tv && (tv.TryGetValue<int>(out var n) ? n == 201 : tv.ToString() == "CustomTheme");
                list.Add((i["name"]?.GetValue<string>() ?? path, path, isTheme));
            }
        }
        return list;
    }

    /// <summary>
    /// Registra os recursos copiados no report.json do destino (formato antigo ou PBIR), sem tirar os
    /// que já estavam lá, e aponta o tema personalizado.
    /// </summary>
    private static void RegisterResources(string reportDir, List<(string Name, string Path, bool IsTheme)> items, string? customTheme)
    {
        var (path, pbir) = PowerBiReport.ReportJson(reportDir);
        if (path == null) throw new InvalidOperationException("Relatório de destino sem report.json — crie as páginas primeiro.");
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException("report.json inválido.");
        var packages = root["resourcePackages"] as JsonArray ?? new JsonArray();
        var themeItem = items.FirstOrDefault(i => i.IsTheme);
        if (pbir)
        {
            if (customTheme != null && themeItem.Path != null)
            {
                var tc = root["themeCollection"] as JsonObject ?? new JsonObject();
                tc["customTheme"] = new JsonObject { ["name"] = themeItem.Name, ["reportVersionAtImport"] = PowerBiReport.VersionAtImport(root), ["type"] = "RegisteredResources" };
                root["themeCollection"] = tc;
            }
            var pkg = packages.FirstOrDefault(p => p?["name"]?.GetValue<string>() == "RegisteredResources") as JsonObject;
            if (pkg == null) packages.Add(pkg = new JsonObject { ["name"] = "RegisteredResources", ["type"] = "RegisteredResources", ["items"] = new JsonArray() });
            var list = pkg["items"] as JsonArray ?? (JsonArray)(pkg["items"] = new JsonArray());
            foreach (var i in items.Where(i => !list.Any(x => x?["path"]?.GetValue<string>() == i.Path)))
                list.Add(new JsonObject { ["name"] = i.Name, ["path"] = i.Path, ["type"] = i.IsTheme ? "CustomTheme" : "Image" });
        }
        else
        {
            if (customTheme != null && themeItem.Path != null)
            {
                var config = ParseString(root["config"]) as JsonObject ?? new JsonObject();
                var tc = config["themeCollection"] as JsonObject ?? new JsonObject();
                tc["customTheme"] = new JsonObject { ["name"] = themeItem.Name, ["version"] = "5.70", ["type"] = 1 };
                config["themeCollection"] = tc;
                root["config"] = config.ToJsonString();
            }
            var pkg = packages.FirstOrDefault(p => p?["resourcePackage"]?["name"]?.GetValue<string>() == "RegisteredResources")?["resourcePackage"] as JsonObject;
            if (pkg == null)
            {
                pkg = new JsonObject { ["name"] = "RegisteredResources", ["type"] = 1, ["disabled"] = false, ["items"] = new JsonArray() };
                packages.Add(new JsonObject { ["resourcePackage"] = pkg });
            }
            var list = pkg["items"] as JsonArray ?? (JsonArray)(pkg["items"] = new JsonArray());
            foreach (var i in items.Where(i => !list.Any(x => x?["path"]?.GetValue<string>() == i.Path)))
                list.Add(new JsonObject { ["type"] = i.IsTheme ? 201 : 100, ["path"] = i.Path, ["name"] = i.Name });
        }
        root["resourcePackages"] = packages;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // ------------------------------------------------------------------ auxiliares

    private static JsonNode? ParseString(JsonNode? node)
    {
        try { return node is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) : node; }
        catch { return null; }
    }

    private static double Num(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    /// <summary>Título do visual (vcObjects/visualContainerObjects → title → text).</summary>
    private static string TitleOf(JsonNode? containerObjects)
    {
        var text = containerObjects?["title"]?[0]?["properties"]?["text"]?["expr"]?["Literal"]?["Value"]?.GetValue<string>();
        return text?.Trim('\'') ?? "";
    }

    /// <summary>Campos usados (queryRef das projeções; no PBIR, queryState).</summary>
    private static IReadOnlyList<string> FieldsOf(JsonNode? node)
    {
        var refs = new List<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                        if (k == "queryRef" && v is JsonValue s && s.TryGetValue<string>(out var r)) refs.Add(r);
                        else Walk(v);
                    break;
                case JsonArray a:
                    foreach (var i in a) Walk(i);
                    break;
            }
        }
        Walk(node);
        return refs.Distinct().ToList();
    }

    /// <summary>Do tema personalizado, só o que define a aparência geral (cores, fontes, fundo).</summary>
    private static JsonNode ThemeEssentials(string json)
    {
        var all = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var keep = new JsonObject();
        foreach (var key in new[] { "name", "dataColors", "background", "foreground", "tableAccent", "good", "neutral", "bad", "textClasses" })
            if (all[key] is { } v) keep[key] = v.DeepClone();
        return keep;
    }

    private static string Compact(JsonNode node, int max)
    {
        var s = node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return s.Length > max ? s[..max] + "…" : s;
    }
}
