// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace NXPilot.Office;

/// <summary>
/// PowerPoint (.pptx) pelo Open XML SDK (MIT), sem Office instalado: o agente descreve os slides
/// em JSON e o NXPilot monta — sem script Python, sem pacote a instalar. Layouts: title, section,
/// bullets, two_columns, table, image, kpis, bars. Cores e fontes vêm do TEMA (cores de esquema e
/// +mj-lt/+mn-lt): no arquivo novo é o tema NX; com um modelo, é o tema do modelo.
/// </summary>
public static class PptxDocument
{
    // 16:9 em EMU (914400 por polegada).
    private const long SlideW = 12192000, SlideH = 6858000, In = 914400, Margin = In / 2;

    public sealed record Palette(string Primary = "123B66", string Accent = "F28C28", string Text = "1B2430", string Font = "Segoe UI")
    {
        public static Palette From(JsonObject? theme) => theme == null ? new() : new(
            Hex(theme["primary"]) ?? "123B66", Hex(theme["accent"]) ?? "F28C28", Hex(theme["text"]) ?? "1B2430",
            theme["font"]?.GetValue<string>() is { Length: > 0 } f ? f : "Segoe UI");

        private static string? Hex(JsonNode? n) =>
            n?.GetValue<string>().TrimStart('#') is { Length: 6 } h && h.All(Uri.IsHexDigit) ? h.ToUpperInvariant() : null;
    }

    // ================================================================ criar e editar

    /// <summary>
    /// Cria <paramref name="output"/> com os slides do JSON. Com <paramref name="template"/> (.pptx),
    /// copia o modelo, tira os slides dele e usa o tema e o layout em branco do modelo.
    /// </summary>
    public static int Build(string output, JsonArray slides, string imageBaseDir, string? template = null, Palette? palette = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (template != null)
        {
            File.Copy(template, output, overwrite: true);
            using var doc = PresentationDocument.Open(output, true);
            var pres = doc.PresentationPart!;
            foreach (var id in SlideIds(pres).ToList()) RemoveSlide(pres, id);
            Append(pres, slides, imageBaseDir, null);
            Doc(pres).Save();
        }
        else
        {
            using var doc = PresentationDocument.Create(output, PresentationDocumentType.Presentation);
            var pres = CreateBase(doc, palette ?? new Palette());
            Append(pres, slides, imageBaseDir, null);
            Doc(pres).Save();
        }
        return slides.Count;
    }

    /// <summary>Acrescenta slides a uma apresentação existente (no fim, ou antes do slide <paramref name="position"/>, 1 = primeiro).</summary>
    public static int AddSlides(string path, JsonArray slides, string imageBaseDir, int? position = null)
    {
        using var doc = PresentationDocument.Open(path, true);
        var pres = doc.PresentationPart!;
        Append(pres, slides, imageBaseDir, position);
        Doc(pres).Save();
        return SlideIds(pres).Count();
    }

    /// <summary>Apaga os slides pelos números (1 = primeiro). Número fora da faixa = erro, nada é apagado.</summary>
    public static int DeleteSlides(string path, IReadOnlyCollection<int> numbers)
    {
        using var doc = PresentationDocument.Open(path, true);
        var pres = doc.PresentationPart!;
        var ids = SlideIds(pres).ToList();
        if (numbers.Any(n => n < 1 || n > ids.Count)) throw new ArgumentException($"Slides válidos: 1 a {ids.Count}.");
        foreach (var n in numbers.Distinct().OrderByDescending(n => n)) RemoveSlide(pres, ids[n - 1]);
        Doc(pres).Save();
        return SlideIds(pres).Count();
    }

    /// <summary>Nova ordem: lista com TODOS os números atuais (ex.: [3,1,2]).</summary>
    public static void Reorder(string path, IReadOnlyList<int> order)
    {
        using var doc = PresentationDocument.Open(path, true);
        var pres = doc.PresentationPart!;
        var list = Doc(pres).SlideIdList!;
        var ids = list.Elements<P.SlideId>().ToList();
        if (order.Count != ids.Count || order.Distinct().Count() != ids.Count || order.Any(n => n < 1 || n > ids.Count))
            throw new ArgumentException($"A ordem precisa ter cada número de 1 a {ids.Count} uma vez.");
        foreach (var id in ids) id.Remove();
        foreach (var n in order) list.Append(ids[n - 1]);
        Doc(pres).Save();
    }

    /// <summary>
    /// Troca texto em todos os parágrafos (formas e tabelas), ou só no slide <paramref name="slide"/>.
    /// O PowerPoint quebra o texto em vários "runs": o parágrafo é lido inteiro, trocado e regravado
    /// no primeiro run (a formatação do parágrafo vem do primeiro run).
    /// </summary>
    public static int ReplaceText(string path, string find, string replace, int? slide = null)
    {
        if (find.Length == 0) throw new ArgumentException("Texto a procurar vazio.");
        using var doc = PresentationDocument.Open(path, true);
        var pres = doc.PresentationPart!;
        var parts = SlideParts(pres).ToList();
        if (slide is { } s && (s < 1 || s > parts.Count)) throw new ArgumentException($"Slides válidos: 1 a {parts.Count}.");
        var count = 0;
        foreach (var (part, i) in parts.Select((p, i) => (p, i + 1)))
        {
            if (slide != null && slide != i) continue;
            var changed = false;
            foreach (var para in SlideOf(part).Descendants<D.Paragraph>())
            {
                var runs = para.Elements<D.Run>().ToList();
                var text = string.Concat(runs.Select(r => r.Text?.Text ?? ""));
                var hits = Occurrences(text, find);
                if (hits == 0) continue;
                count += hits;
                changed = true;
                runs[0].Text ??= new D.Text();
                runs[0].Text!.Text = text.Replace(find, replace, StringComparison.Ordinal);
                foreach (var extra in runs.Skip(1)) extra.Remove();
            }
            if (changed) SlideOf(part).Save();
        }
        return count;
    }

    // ================================================================ ler

    /// <summary>Texto da apresentação em markdown: slide a slide, formas com nome, tabelas e imagens.</summary>
    public static string ReadAsMarkdown(string path, int maxChars = 60_000)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart!;
        var sb = new StringBuilder();
        var parts = SlideParts(pres).ToList();
        sb.AppendLine($"# {Path.GetFileName(path)} — {parts.Count} slide(s)");
        foreach (var (part, i) in parts.Select((p, i) => (p, i + 1)))
        {
            sb.AppendLine().AppendLine($"## Slide {i}");
            foreach (var el in SlideOf(part).CommonSlideData?.ShapeTree?.Elements() ?? [])
                Describe(el, sb);
            if (part.NotesSlidePart?.NotesSlide is { } notes)
            {
                var noteText = string.Join(" ", notes.Descendants<D.Paragraph>().Select(ParagraphText).Where(t => t.Length > 0 && !int.TryParse(t, out _)));
                if (noteText.Length > 0) sb.AppendLine($"> Notas: {noteText}");
            }
            if (sb.Length > maxChars) { sb.AppendLine("…(cortado)"); break; }
        }
        return sb.ToString();
    }

    private static void Describe(OpenXmlElement el, StringBuilder sb)
    {
        switch (el)
        {
            case P.Shape sp when sp.TextBody != null:
                var paras = sp.TextBody.Elements<D.Paragraph>().Select(p => (Level: p.ParagraphProperties?.Level?.Value ?? 0, Text: ParagraphText(p)))
                    .Where(p => p.Text.Length > 0).ToList();
                if (paras.Count == 0) return;
                sb.AppendLine($"[{sp.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value}]");
                foreach (var (level, text) in paras) sb.AppendLine(new string(' ', level * 2) + "- " + text);
                break;
            case P.GraphicFrame gf when gf.Descendants<D.Table>().FirstOrDefault() is { } table:
                sb.AppendLine($"[{gf.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value}]");
                var rows = table.Elements<D.TableRow>().Select(r => r.Elements<D.TableCell>()
                    .Select(c => string.Join(" ", c.Descendants<D.Paragraph>().Select(ParagraphText)).Replace("|", "\\|")).ToList()).ToList();
                for (var r = 0; r < rows.Count; r++)
                {
                    sb.AppendLine("| " + string.Join(" | ", rows[r]) + " |");
                    if (r == 0) sb.AppendLine("|" + string.Concat(rows[0].Select(_ => " --- |")));
                }
                break;
            case P.Picture pic:
                var nv = pic.NonVisualPictureProperties?.NonVisualDrawingProperties;
                sb.AppendLine($"[imagem: {nv?.Description?.Value ?? nv?.Name?.Value}]");
                break;
            case P.GroupShape group:
                foreach (var child in group.Elements()) Describe(child, sb);
                break;
        }
    }

    private static string ParagraphText(D.Paragraph p) => string.Concat(p.Elements<D.Run>().Select(r => r.Text?.Text ?? "")).Trim();

    /// <summary>Erros de esquema (Open XML) — o arquivo que o NXPilot gera precisa sair com zero.</summary>
    public static IReadOnlyList<string> Validate(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        return new OpenXmlValidator().Validate(doc).Take(20).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
    }

    public static int SlideCount(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        return SlideIds(doc.PresentationPart!).Count();
    }

    // ================================================================ base (arquivo novo)

    private static PresentationPart CreateBase(PresentationDocument doc, Palette pal)
    {
        var pres = doc.AddPresentationPart();
        pres.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rId1" }),
            new P.SlideIdList(),
            new P.SlideSize { Cx = (int)SlideW, Cy = (int)SlideH },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 },
            new P.DefaultTextStyle());

        var master = pres.AddNewPart<SlideMasterPart>("rId1");
        var theme = master.AddNewPart<ThemePart>("rId2");
        theme.Theme = Theme(pal);
        pres.AddPart(theme, "rId2");

        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        layout.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(EmptyTree()) { Name = "Blank" },
            new P.ColorMapOverride(new D.MasterColorMapping()))
        { Type = P.SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master, "rId1");

        master.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(
                new P.Background(new P.BackgroundProperties(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.Background1 }), new D.EffectList())),
                EmptyTree()),
            new P.ColorMap
            {
                Background1 = D.ColorSchemeIndexValues.Light1, Text1 = D.ColorSchemeIndexValues.Dark1,
                Background2 = D.ColorSchemeIndexValues.Light2, Text2 = D.ColorSchemeIndexValues.Dark2,
                Accent1 = D.ColorSchemeIndexValues.Accent1, Accent2 = D.ColorSchemeIndexValues.Accent2,
                Accent3 = D.ColorSchemeIndexValues.Accent3, Accent4 = D.ColorSchemeIndexValues.Accent4,
                Accent5 = D.ColorSchemeIndexValues.Accent5, Accent6 = D.ColorSchemeIndexValues.Accent6,
                Hyperlink = D.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = "rId1" }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        return pres;
    }

    /// <summary>Tema NX: dk2 = cor principal (capa e títulos), accent1 = destaque azul, accent2 = laranja.</summary>
    private static D.Theme Theme(Palette pal)
    {
        static D.RgbColorModelHex Rgb(string hex) => new() { Val = hex };
        static D.SolidFill Ph() => new(new D.SchemeColor { Val = D.SchemeColorValues.PhColor });
        static D.Outline Line(int w) => new(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })) { Width = w };
        D.MajorFont Major() => new(new D.LatinFont { Typeface = pal.Font }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" });
        D.MinorFont Minor() => new(new D.LatinFont { Typeface = pal.Font }, new D.EastAsianFont { Typeface = "" }, new D.ComplexScriptFont { Typeface = "" });

        return new D.Theme(
            new D.ThemeElements(
                new D.ColorScheme(
                    new D.Dark1Color(Rgb(pal.Text)), new D.Light1Color(Rgb("FFFFFF")),
                    new D.Dark2Color(Rgb(pal.Primary)), new D.Light2Color(Rgb("EEF2F7")),
                    new D.Accent1Color(Rgb("1F6FB2")), new D.Accent2Color(Rgb(pal.Accent)),
                    new D.Accent3Color(Rgb("2BA36B")), new D.Accent4Color(Rgb("D64545")),
                    new D.Accent5Color(Rgb("7A5AF8")), new D.Accent6Color(Rgb("8A94A6")),
                    new D.Hyperlink(Rgb("1F6FB2")), new D.FollowedHyperlinkColor(Rgb("7A5AF8")))
                { Name = "NX" },
                new D.FontScheme(Major(), Minor()) { Name = "NX" },
                new D.FormatScheme(
                    new D.FillStyleList(Ph(), Ph(), Ph()),
                    new D.LineStyleList(Line(6350), Line(12700), Line(19050)),
                    new D.EffectStyleList(new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
                    new D.BackgroundFillStyleList(Ph(), Ph(), Ph()))
                { Name = "NX" }),
            new D.ObjectDefaults(),
            new D.ExtraColorSchemeList())
        { Name = "NX" };
    }

    private static P.ShapeTree EmptyTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new D.TransformGroup()));

    // ================================================================ slides

    private static P.Presentation Doc(PresentationPart pres) => pres.Presentation ?? throw new InvalidOperationException("Apresentação sem presentation.xml.");
    private static P.Slide SlideOf(SlidePart part) => part.Slide ?? throw new InvalidOperationException("Slide vazio.");

    private static IEnumerable<P.SlideId> SlideIds(PresentationPart pres) =>
        Doc(pres).SlideIdList?.Elements<P.SlideId>() ?? [];

    private static IEnumerable<SlidePart> SlideParts(PresentationPart pres) =>
        SlideIds(pres).Select(id => (SlidePart)pres.GetPartById(id.RelationshipId!));

    private static void RemoveSlide(PresentationPart pres, P.SlideId id)
    {
        var part = pres.GetPartById(id.RelationshipId!);
        id.Remove();
        pres.DeletePart(part);
    }

    /// <summary>Layout em branco do mestre (o modelo pode não ter: usa o de menos marcadores).</summary>
    private static SlideLayoutPart BlankLayout(PresentationPart pres)
    {
        var layouts = pres.SlideMasterParts.SelectMany(m => m.SlideLayoutParts).ToList();
        if (layouts.Count == 0) throw new InvalidOperationException("A apresentação não tem layout de slide.");
        return layouts.FirstOrDefault(l => l.SlideLayout?.Type?.Value == P.SlideLayoutValues.Blank)
               ?? layouts.OrderBy(l => l.SlideLayout?.Descendants<P.PlaceholderShape>().Count() ?? 0).First();
    }

    private static void Append(PresentationPart pres, JsonArray slides, string imageBaseDir, int? position)
    {
        var layout = BlankLayout(pres);
        var list = Doc(pres).SlideIdList ??= new P.SlideIdList();
        var nextId = SlideIds(pres).Select(s => s.Id?.Value ?? 0).DefaultIfEmpty(255U).Max() + 1;
        var existing = list.Elements<P.SlideId>().ToList();
        var anchor = position is { } pos && pos >= 1 && pos <= existing.Count ? existing[pos - 1] : null;
        var total = existing.Count + slides.Count;

        foreach (var (node, i) in slides.Select((n, i) => (n, i)))
        {
            if (node is not JsonObject spec) throw new ArgumentException($"Slide {i + 1}: esperado um objeto JSON.");
            var part = pres.AddNewPart<SlidePart>();
            part.AddPart(layout);
            var number = (anchor != null ? existing.IndexOf(anchor) : existing.Count) + i + 1;
            part.Slide = new SlideCanvas(part, imageBaseDir).Render(spec, number, total);
            var id = new P.SlideId { Id = nextId++, RelationshipId = pres.GetIdOfPart(part) };
            if (anchor != null) list.InsertBefore(id, anchor); else list.Append(id);
        }
    }

    private static int Occurrences(string text, string find)
    {
        var n = 0;
        for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Desenha um slide: cada layout posiciona formas nativas (texto, tabela, imagem, barras).</summary>
    private sealed class SlideCanvas(SlidePart part, string imageBaseDir)
    {
        private readonly P.ShapeTree _tree = EmptyTree();
        private uint _nextShape = 2;
        private D.SchemeColorValues? _background;

        public P.Slide Render(JsonObject s, int number, int total)
        {
            var layout = (Str(s, "layout") ?? "bullets").ToLowerInvariant();
            var title = Str(s, "title") ?? "";
            switch (layout)
            {
                case "title":
                    _background = D.SchemeColorValues.Text2;
                    Bar(Margin + In / 4, (long)(In * 2.35), In * 3 / 2, In / 12, D.SchemeColorValues.Accent2);
                    Text("Título", Margin + In / 4, (long)(In * 2.55), SlideW - 2 * Margin - In / 2, In * 3 / 2,
                        [Para(title, 44, true, D.SchemeColorValues.Background1, major: true)], D.TextAnchoringTypeValues.Bottom);
                    if (Str(s, "subtitle") is { } sub)
                        Text("Subtítulo", Margin + In / 4, (long)(In * 4.15), SlideW - 2 * Margin - In / 2, In,
                            [Para(sub, 20, false, D.SchemeColorValues.Background2)], D.TextAnchoringTypeValues.Top);
                    if (Str(s, "footer") is { } foot)
                        Text("Rodapé", Margin + In / 4, SlideH - In, SlideW - 2 * Margin, In / 2,
                            [Para(foot, 12, false, D.SchemeColorValues.Background2)], D.TextAnchoringTypeValues.Center);
                    break;
                case "section":
                    _background = D.SchemeColorValues.Accent1;
                    Text("Seção", Margin + In / 4, In * 2, SlideW - 2 * Margin - In / 2, In * 2,
                        [Para(title, 36, true, D.SchemeColorValues.Background1, major: true)], D.TextAnchoringTypeValues.Bottom);
                    if (Str(s, "subtitle") is { } secSub)
                        Text("Subtítulo", Margin + In / 4, In * 4, SlideW - 2 * Margin - In / 2, In,
                            [Para(secSub, 18, false, D.SchemeColorValues.Background1)], D.TextAnchoringTypeValues.Top);
                    break;
                case "bullets":
                    Header(title);
                    Bullets("Tópicos", Margin, In * 3 / 2, SlideW - 2 * Margin, SlideH - In * 2, s["bullets"] as JsonArray ?? s["items"] as JsonArray);
                    Footer(number, total, s);
                    break;
                case "two_columns":
                    Header(title);
                    var colW = (SlideW - 2 * Margin - In / 2) / 2;
                    Column("Esquerda", Margin, colW, Str(s, "left_title"), s["left"] as JsonArray);
                    Column("Direita", Margin + colW + In / 2, colW, Str(s, "right_title"), s["right"] as JsonArray);
                    Footer(number, total, s);
                    break;
                case "table":
                    Header(title);
                    Table(s["columns"] as JsonArray, s["rows"] as JsonArray);
                    Footer(number, total, s);
                    break;
                case "image":
                    Header(title);
                    Image(Str(s, "image") ?? throw new ArgumentException($"Slide {number}: layout image precisa de \"image\"."), Str(s, "caption"));
                    Footer(number, total, s);
                    break;
                case "kpis":
                    Header(title);
                    Kpis(s["items"] as JsonArray);
                    Footer(number, total, s);
                    break;
                case "bars":
                    Header(title);
                    Bars(s["items"] as JsonArray, Str(s, "unit"));
                    Footer(number, total, s);
                    break;
                default:
                    throw new ArgumentException($"Slide {number}: layout \"{layout}\" não existe (title, section, bullets, two_columns, table, image, kpis, bars).");
            }

            var data = new P.CommonSlideData();
            if (_background is { } bg)
                data.Append(new P.Background(new P.BackgroundProperties(new D.SolidFill(new D.SchemeColor { Val = bg }), new D.EffectList())));
            data.Append(_tree);
            return new P.Slide(data, new P.ColorMapOverride(new D.MasterColorMapping()));
        }

        // ------------------------------------------------------------ blocos dos layouts

        private void Header(string title)
        {
            Text("Título", Margin, In * 3 / 8, SlideW - 2 * Margin, In * 7 / 8,
                [Para(title, 28, true, D.SchemeColorValues.Text2, major: true)], D.TextAnchoringTypeValues.Bottom);
            Bar(Margin + 91440, In * 5 / 4, In, In / 20, D.SchemeColorValues.Accent2);
        }

        private void Footer(int number, int total, JsonObject s)
        {
            Text("Número", SlideW - Margin - In * 2, SlideH - In / 2, In * 2, In * 3 / 8,
                [Para($"{number} / {total}", 10, false, D.SchemeColorValues.Accent6, align: D.TextAlignmentTypeValues.Right)], D.TextAnchoringTypeValues.Center);
            if (Str(s, "footer") is { } foot)
                Text("Rodapé", Margin, SlideH - In / 2, SlideW - 2 * Margin - In * 2, In * 3 / 8,
                    [Para(foot, 10, false, D.SchemeColorValues.Accent6)], D.TextAnchoringTypeValues.Center);
        }

        private void Bullets(string name, long x, long y, long w, long h, JsonArray? items)
        {
            var list = (items ?? []).Select(BulletItem).ToList();
            var lines = list.Sum(b => 1 + b.Text.Length / 70);
            var size = lines > 12 ? 14 : lines > 9 ? 16 : lines > 7 ? 18 : 20;
            Text(name, x, y, w, h, list.Select(b => BulletPara(b.Text, b.Level, b.Level == 0 ? size : size - 2)).ToList(), D.TextAnchoringTypeValues.Top);
        }

        private void Column(string name, long x, long w, string? heading, JsonArray? items)
        {
            var y = In * 3 / 2;
            if (heading != null)
            {
                Text(name + " título", x, y, w, In / 2, [Para(heading, 20, true, D.SchemeColorValues.Accent1, major: true)], D.TextAnchoringTypeValues.Bottom);
                y += In * 5 / 8;
            }
            Bullets(name, x, y, w, SlideH - y - In / 2, items);
        }

        private void Table(JsonArray? columns, JsonArray? rows)
        {
            var header = (columns ?? []).Select(c => c?.ToString() ?? "").ToList();
            var body = (rows ?? []).Select(r => (r as JsonArray ?? []).Select(c => c?.ToString() ?? "").ToList()).ToList();
            var cols = Math.Max(header.Count, body.Select(r => r.Count).DefaultIfEmpty(0).Max());
            if (cols == 0) throw new ArgumentException("Tabela sem colunas.");
            var w = SlideW - 2 * Margin;
            var rowCount = body.Count + (header.Count > 0 ? 1 : 0);
            var rowH = Math.Min(In / 2, (SlideH - In * 2) / Math.Max(1, rowCount));
            var size = rowCount > 12 ? 10 : rowCount > 8 ? 12 : 14;

            // Largura proporcional ao texto mais longo de cada coluna (mínimo de 8%).
            var weights = Enumerable.Range(0, cols)
                .Select(c => Math.Max(4L, new[] { header }.Concat(body).Max(r => c < r.Count ? Math.Min(40, r[c].Length) : 0))).ToList();
            var sum = weights.Sum();
            var widths = weights.Select(x => Math.Max(w * 8 / 100, w * x / sum)).ToList();
            widths[^1] += w - widths.Sum();

            var table = new D.Table(new D.TableProperties { FirstRow = header.Count > 0, BandRow = true },
                new D.TableGrid(widths.Select(cw => new D.GridColumn { Width = cw })));
            if (header.Count > 0) table.Append(Row(header, cols, rowH, size, true, D.SchemeColorValues.Text2));
            foreach (var (r, i) in body.Select((r, i) => (r, i)))
                table.Append(Row(r, cols, rowH, size, false, i % 2 == 0 ? D.SchemeColorValues.Background2 : D.SchemeColorValues.Background1));

            _tree.Append(new P.GraphicFrame(
                new P.NonVisualGraphicFrameProperties(
                    new P.NonVisualDrawingProperties { Id = _nextShape++, Name = "Tabela" },
                    new P.NonVisualGraphicFrameDrawingProperties(new D.GraphicFrameLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.Transform(new D.Offset { X = Margin, Y = In * 3 / 2 }, new D.Extents { Cx = w, Cy = rowH * rowCount }),
                new D.Graphic(new D.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" })));
        }

        private static D.TableRow Row(List<string> cells, int cols, long h, int size, bool header, D.SchemeColorValues fill)
        {
            var row = new D.TableRow { Height = h };
            for (var c = 0; c < cols; c++)
            {
                var text = c < cells.Count ? cells[c] : "";
                row.Append(new D.TableCell(
                    new D.TextBody(new D.BodyProperties(), new D.ListStyle(),
                        Para(text, size, header, header ? D.SchemeColorValues.Background1 : D.SchemeColorValues.Text1)),
                    new D.TableCellProperties(
                        new D.LeftBorderLineProperties(new D.NoFill()),
                        new D.RightBorderLineProperties(new D.NoFill()),
                        new D.TopBorderLineProperties(new D.NoFill()),
                        new D.BottomBorderLineProperties(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.Accent6 })) { Width = 3175 },
                        new D.SolidFill(new D.SchemeColor { Val = fill }))
                    { LeftMargin = 91440, RightMargin = 91440, Anchor = D.TextAnchoringTypeValues.Center }));
            }
            return row;
        }

        private void Image(string relative, string? caption)
        {
            var path = Path.GetFullPath(Path.Combine(imageBaseDir, relative));
            if (!File.Exists(path)) throw new FileNotFoundException($"Imagem não encontrada: {relative}");
            var bytes = File.ReadAllBytes(path);
            var (pw, ph, type) = ImageSize(bytes) ?? throw new ArgumentException($"Imagem não suportada (use PNG ou JPEG): {relative}");
            var img = part.AddImagePart(type);
            using (var ms = new MemoryStream(bytes)) img.FeedData(ms);

            // Cabe na área útil mantendo a proporção, centralizada.
            long boxX = Margin, boxY = In * 3 / 2, boxW = SlideW - 2 * Margin, boxH = SlideH - In * 2 - (caption != null ? In / 2 : 0);
            var scale = Math.Min((double)boxW / pw, (double)boxH / ph);
            long w = (long)(pw * scale), h = (long)(ph * scale);
            _tree.Append(new P.Picture(
                new P.NonVisualPictureProperties(
                    new P.NonVisualDrawingProperties { Id = _nextShape++, Name = "Imagem", Description = caption ?? Path.GetFileName(relative) },
                    new P.NonVisualPictureDrawingProperties(new D.PictureLocks { NoChangeAspect = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.BlipFill(new D.Blip { Embed = part.GetIdOfPart(img) }, new D.Stretch(new D.FillRectangle())),
                new P.ShapeProperties(
                    new D.Transform2D(new D.Offset { X = boxX + (boxW - w) / 2, Y = boxY + (boxH - h) / 2 }, new D.Extents { Cx = w, Cy = h }),
                    new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle })));
            if (caption != null)
                Text("Legenda", Margin, boxY + boxH + In / 16, boxW, In * 3 / 8,
                    [Para(caption, 12, false, D.SchemeColorValues.Accent6, align: D.TextAlignmentTypeValues.Center)], D.TextAnchoringTypeValues.Top);
        }

        private void Kpis(JsonArray? items)
        {
            var list = (items ?? []).OfType<JsonObject>().Take(6).ToList();
            if (list.Count == 0) throw new ArgumentException("kpis precisa de \"items\": [{\"label\", \"value\"}].");
            var perRow = list.Count <= 4 ? list.Count : 3;
            var rows = (list.Count + perRow - 1) / perRow;
            const long gap = In / 4;
            var w = (SlideW - 2 * Margin - gap * (perRow - 1)) / perRow;
            var h = Math.Min(In * 2, (SlideH - In * 2 - gap * (rows - 1)) / rows);
            for (var i = 0; i < list.Count; i++)
            {
                var x = Margin + (i % perRow) * (w + gap);
                var y = In * 3 / 2 + (SlideH - In * 2 - rows * h - gap * (rows - 1)) / 2 + (i / perRow) * (h + gap);
                var color = Str(list[i], "trend") switch
                {
                    "up" or "good" => D.SchemeColorValues.Accent3,
                    "down" or "bad" => D.SchemeColorValues.Accent4,
                    _ => D.SchemeColorValues.Accent1,
                };
                var paras = new List<D.Paragraph>
                {
                    Para(Str(list[i], "value") ?? "", 36, true, color, align: D.TextAlignmentTypeValues.Center, major: true),
                    Para(Str(list[i], "label") ?? "", 14, false, D.SchemeColorValues.Text1, align: D.TextAlignmentTypeValues.Center),
                };
                if (Str(list[i], "detail") is { } detail) paras.Add(Para(detail, 11, false, D.SchemeColorValues.Accent6, align: D.TextAlignmentTypeValues.Center));
                Text("KPI " + (i + 1), x, y, w, h, paras, D.TextAnchoringTypeValues.Center, D.SchemeColorValues.Background2, D.ShapeTypeValues.RoundRectangle);
            }
        }

        private void Bars(JsonArray? items, string? unit)
        {
            var list = (items ?? []).OfType<JsonObject>().Take(12)
                .Select(o => (Label: Str(o, "label") ?? "", Value: o["value"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.TryParse(Str(o, "value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0))
                .ToList();
            if (list.Count == 0) throw new ArgumentException("bars precisa de \"items\": [{\"label\", \"value\"}].");
            var max = Math.Max(list.Max(b => b.Value), 1e-9);
            long labelW = In * 3, valueW = In * 3 / 2, top = In * 3 / 2;
            var rowH = Math.Min(In * 5 / 8, (SlideH - top - In * 3 / 4) / list.Count);
            var barMax = SlideW - 2 * Margin - labelW - valueW;
            for (var i = 0; i < list.Count; i++)
            {
                var y = top + i * rowH;
                Text("Rótulo " + (i + 1), Margin, y, labelW - In / 8, rowH, [Para(list[i].Label, 14, false, D.SchemeColorValues.Text1, align: D.TextAlignmentTypeValues.Right)], D.TextAnchoringTypeValues.Center);
                var bw = Math.Max(In / 20, (long)(barMax * Math.Max(0, list[i].Value) / max));
                Bar(Margin + labelW, y + rowH / 6, bw, rowH * 2 / 3, D.SchemeColorValues.Accent1);
                var label = list[i].Value.ToString("#,0.##", System.Globalization.CultureInfo.CurrentCulture) + (unit != null ? " " + unit : "");
                Text("Valor " + (i + 1), Margin + labelW + bw + In / 16, y, valueW, rowH, [Para(label, 14, true, D.SchemeColorValues.Text2)], D.TextAnchoringTypeValues.Center);
            }
        }

        // ------------------------------------------------------------ formas

        private void Bar(long x, long y, long w, long h, D.SchemeColorValues color) =>
            _tree.Append(new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = _nextShape++, Name = "Barra " + _nextShape },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(
                    new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
                    new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                    new D.SolidFill(new D.SchemeColor { Val = color }),
                    new D.Outline(new D.NoFill()))));

        private void Text(string name, long x, long y, long w, long h, IReadOnlyList<D.Paragraph> paragraphs, D.TextAnchoringTypeValues anchor,
            D.SchemeColorValues? fill = null, D.ShapeTypeValues? geometry = null)
        {
            var spPr = new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = geometry ?? D.ShapeTypeValues.Rectangle });
            spPr.Append(fill is { } f ? new D.SolidFill(new D.SchemeColor { Val = f }) : new D.NoFill());
            spPr.Append(new D.Outline(new D.NoFill()));
            var body = new P.TextBody(
                new D.BodyProperties(new D.NormalAutoFit()) { Wrap = D.TextWrappingValues.Square, Anchor = anchor, LeftInset = 91440, RightInset = 91440, TopInset = 45720, BottomInset = 45720 },
                new D.ListStyle());
            foreach (var p in paragraphs) body.Append(p);
            _tree.Append(new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = _nextShape++, Name = name },
                    new P.NonVisualShapeDrawingProperties { TextBox = true },
                    new P.ApplicationNonVisualDrawingProperties()),
                spPr, body));
        }

        private static D.Paragraph Para(string text, int size, bool bold, D.SchemeColorValues color,
            D.TextAlignmentTypeValues? align = null, bool major = false)
        {
            var p = new D.Paragraph(new D.ParagraphProperties { Alignment = align ?? D.TextAlignmentTypeValues.Left });
            foreach (var r in Runs(text, size, bold, color, major)) p.Append(r);
            p.Append(new D.EndParagraphRunProperties { Language = "pt-BR", FontSize = size * 100 });
            return p;
        }

        private static D.Paragraph BulletPara(string text, int level, int size)
        {
            var pPr = new D.ParagraphProperties(
                new D.SpaceBefore(new D.SpacingPoints { Val = level == 0 ? 900 : 300 }),
                new D.BulletColor(new D.SchemeColor { Val = level == 0 ? D.SchemeColorValues.Accent2 : D.SchemeColorValues.Accent6 }),
                new D.BulletFont { Typeface = "Arial" },
                new D.CharacterBullet { Char = level == 0 ? "■" : "–" })
            { Level = level, LeftMargin = 342900 + level * 342900, Indent = -342900 };
            var p = new D.Paragraph(pPr);
            foreach (var r in Runs(text, size, false, D.SchemeColorValues.Text1, false)) p.Append(r);
            p.Append(new D.EndParagraphRunProperties { Language = "pt-BR", FontSize = size * 100 });
            return p;
        }

        /// <summary>Texto com **negrito** (markdown simples) vira runs alternando o negrito.</summary>
        private static IEnumerable<D.Run> Runs(string text, int size, bool bold, D.SchemeColorValues color, bool major)
        {
            var pieces = text.Split("**");
            for (var i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].Length == 0) continue;
                var rPr = new D.RunProperties(new D.SolidFill(new D.SchemeColor { Val = color }))
                { Language = "pt-BR", FontSize = size * 100, Bold = bold || i % 2 == 1, Dirty = false };
                rPr.Append(new D.LatinFont { Typeface = major ? "+mj-lt" : "+mn-lt" });
                yield return new D.Run(rPr, new D.Text(pieces[i]));
            }
        }

        private static (string Text, int Level) BulletItem(JsonNode? n) => n switch
        {
            JsonObject o => (Str(o, "text") ?? "", Math.Clamp(o["level"] is JsonValue v && v.TryGetValue<int>(out var l) ? l : 0, 0, 3)),
            _ => (n?.ToString() ?? "", 0),
        };
    }

    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var other => other.ToString(),
    };

    /// <summary>Largura/altura em pixels lidas do cabeçalho (PNG ou JPEG).</summary>
    internal static (int W, int H, PartTypeInfo Type)? ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23], ImagePartType.Png);
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6], ImagePartType.Jpeg);
                i += 2 + len;
            }
        }
        return null;
    }
}
