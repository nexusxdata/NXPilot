// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NXPilot.Integrations;

/// <summary>
/// Extensão PowerPoint: de onde vêm as figuras. Sem chave de API: pasta de imagens (local), Openverse
/// e Wikimedia Commons — só licenças que permitem usar em aula. Fica na pasta das extensões (local).
/// </summary>
public sealed class PowerPointSettings
{
    /// <summary>Pasta de imagens (ex.: logo, fotos de eventos): procurada primeiro, sem internet.</summary>
    public string ImagesFolder { get; set; } = "";
    public bool UseOpenverse { get; set; } = true;
    public bool UseWikimedia { get; set; } = true;
    /// <summary>Reserva: quando uma fonte não responde ou vêm poucas figuras, tenta a NASA (domínio público; ciências e astronomia).</summary>
    public bool UseNasaFallback { get; set; } = true;
    /// <summary>Licenças "não comercial" (CC BY-NC…): servem para aula; desligue para material vendido.</summary>
    public bool AllowNonCommercial { get; set; } = true;

    private static string FilePath => ExtensionSettings.File("powerpoint.json");

    public static PowerPointSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<PowerPointSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Uma figura encontrada: onde ver, quem fez e com que licença (para o crédito).</summary>
public sealed record ImageHit(
    string Source, string Title, string ThumbUrl, string ImageUrl, string PageUrl,
    string Author, string License, string LicenseUrl, int Width, int Height, string? LocalPath = null)
{
    /// <summary>Linha de crédito pronta: "Título — Autor · Licença · Fonte (link)".</summary>
    public string Credit => LocalPath != null
        ? $"{Title} — " + (Source == OwnSource ? NxText.T("imagem própria", "own image", "imagen propia") : NxText.T("pasta de imagens", "images folder", "carpeta de imágenes"))
        : $"{Title} — {(Author.Length > 0 ? Author : NxText.T("autor não informado", "unknown author", "autor no informado"))} · {License} · {Source} ({PageUrl})";

    /// <summary>Fonte das imagens que a pessoa trouxe do computador dela na hora da escolha.</summary>
    public const string OwnSource = "Imagem própria";

    /// <summary>Imagem do computador da pessoa (escolhida ou arrastada na janela).</summary>
    public static ImageHit Own(string path) => new(OwnSource, Path.GetFileNameWithoutExtension(path), path, path, path, "", "", "", 0, 0, path);

    /// <summary>Texto alternativo (acessibilidade): o título da obra, sem extensão nem "File:".</summary>
    public string AltText => Title;
}

/// <summary>Busca de figuras para slides: pasta de imagens, Openverse e Wikimedia Commons (sem chave).</summary>
public static class ImageSearch
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        // A Wikimedia pede um User-Agent que identifique o app.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NXPilot/1.0 (+https://github.com/nexusxdata/NXPilot)");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private static readonly string[] LocalExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>Fontes que não responderam na última busca (a janela avisa; as outras seguem).</summary>
    public static IReadOnlyList<string> LastErrors { get; private set; } = [];

    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Até <paramref name="count"/> figuras de cada fonte, juntas (para o agente; a janela usa a versão progressiva).</summary>
    public static async Task<List<ImageHit>> SearchAsync(string query, int count, PowerPointSettings settings, CancellationToken ct = default)
    {
        var hits = new List<ImageHit>();
        await SearchProgressiveAsync(query, count, settings, batch => { lock (hits) hits.AddRange(batch); }, ct);
        if (hits.Count == 0 && LastErrors.Count > 0) throw new InvalidOperationException(string.Join(" · ", LastErrors));
        return hits.GroupBy(h => h.ImageUrl).Select(g => g.First()).Take(Math.Clamp(count, 1, 40) * 3).ToList();
    }

    /// <summary>
    /// Busca progressiva: pasta de imagens na hora; Openverse e Wikimedia ao mesmo tempo, cada uma com
    /// até 10 s, entregando o que achar assim que responder (<paramref name="onBatch"/>); se uma fonte
    /// não respondeu ou vieram poucas figuras, tenta o acervo reserva (NASA).
    /// </summary>
    public static async Task SearchProgressiveAsync(string query, int count, PowerPointSettings settings, Action<List<ImageHit>> onBatch, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 40);
        var errors = new List<string>();
        var total = 0;
        void Deliver(List<ImageHit> batch)
        {
            batch = batch.Where(SharpEnough).ToList();
            if (batch.Count == 0) return;
            Interlocked.Add(ref total, batch.Count);
            onBatch(batch);
        }
        async Task Guarded(string name, Func<CancellationToken, Task<List<ImageHit>>> source)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SourceTimeout);
            try { Deliver(await source(cts.Token)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
            {
                ct.ThrowIfCancellationRequested();
                lock (errors) errors.Add(name + ": " + (ex is TaskCanceledException or OperationCanceledException ? NxText.T("não respondeu em 10 s", "no answer in 10 s", "no respondió en 10 s") : ex.Message));
            }
        }
        Deliver(SearchFolder(settings.ImagesFolder, query, count).ToList());
        var tasks = new List<Task>();
        if (settings.UseOpenverse) tasks.Add(Guarded("Openverse", t => OpenverseAsync(query, count, settings.AllowNonCommercial, t)));
        if (settings.UseWikimedia) tasks.Add(Guarded("Wikimedia Commons", t => WikimediaAsync(query, count, settings.AllowNonCommercial, t)));
        await Task.WhenAll(tasks);
        if (settings.UseNasaFallback && (errors.Count > 0 || total < count))
            await Guarded("NASA", t => NasaAsync(query, count, t));
        LastErrors = errors;
    }

    /// <summary>Largura mínima (px) de figura da internet: menor que isso fica borrada projetada no slide.</summary>
    public const int MinWidth = 800;

    /// <summary>Figura da internet com tamanho conhecido abaixo de <see cref="MinWidth"/> fica de fora; as da pasta de imagens sempre entram.</summary>
    public static bool SharpEnough(ImageHit hit) => hit.LocalPath != null || hit.Width <= 0 || hit.Width >= MinWidth;

    /// <summary>Arquivos da pasta de imagens cujo nome (ou pasta) tem os termos da busca, sem diferenciar acentos.</summary>
    public static IEnumerable<ImageHit> SearchFolder(string folder, string query, int count)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2).ToList();
        if (terms.Count == 0) return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => LocalExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => terms.Any(t => Fold(Path.GetRelativePath(folder, f)).Contains(t)))
                .Take(count)
                .Select(f => new ImageHit("Pasta de imagens", Path.GetFileNameWithoutExtension(f), f, f, f, "", "", "", 0, 0, f))
                .ToList();
        }
        catch { return []; }
    }

    private static async Task<List<ImageHit>> OpenverseAsync(string query, int count, bool allowNc, CancellationToken ct)
    {
        var licenses = allowNc ? "cc0,pdm,by,by-sa,by-nc,by-nc-sa" : "cc0,pdm,by,by-sa";
        var url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&page_size={count}&mature=false&license={licenses}";
        return ParseOpenverse(await Http.GetStringAsync(url, ct));
    }

    private static async Task<List<ImageHit>> WikimediaAsync(string query, int count, bool allowNc, CancellationToken ct)
    {
        var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6" +
                  $"&gsrlimit={count}&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}" +
                  "&prop=imageinfo&iiprop=url|size|extmetadata&iiurlwidth=360&iiextmetadatafilter=Artist|LicenseShortName|LicenseUrl|ObjectName";
        return ParseWikimedia(await Http.GetStringAsync(url, ct), allowNc);
    }

    private static async Task<List<ImageHit>> NasaAsync(string query, int count, CancellationToken ct) =>
        ParseNasa(await Http.GetStringAsync($"https://images-api.nasa.gov/search?q={Uri.EscapeDataString(query)}&media_type=image&page_size={count}", ct));

    /// <summary>Resposta da API de imagens da NASA → figuras (domínio público; público para o teste).</summary>
    public static List<ImageHit> ParseNasa(string json)
    {
        var list = new List<ImageHit>();
        foreach (var item in JsonNode.Parse(json)?["collection"]?["items"] as JsonArray ?? [])
        {
            if (item?["data"]?[0] is not JsonObject data || Str(data, "nasa_id") is not { Length: > 0 } id) continue;
            var thumb = (item["links"] as JsonArray ?? []).Select(l => l?["href"]?.ToString()).FirstOrDefault(h => h != null && h.Contains("~thumb")) ?? "";
            if (thumb.Length == 0) continue;
            var author = Str(data, "secondary_creator") is { Length: > 0 } c ? c : "NASA" + (Str(data, "center") is { Length: > 0 } center ? "/" + center : "");
            list.Add(new ImageHit("NASA", Clean(Str(data, "title") ?? id), thumb, thumb.Replace("~thumb", "~large"),
                $"https://images.nasa.gov/details/{Uri.EscapeDataString(id)}", Clean(author),
                NxText.T("Domínio público (NASA)", "Public domain (NASA)", "Dominio público (NASA)"), "https://www.nasa.gov/nasa-brand-center/images-and-media/", 0, 0));
        }
        return list;
    }

    /// <summary>Resposta da API do Openverse → figuras (público para o teste).</summary>
    public static List<ImageHit> ParseOpenverse(string json)
    {
        var list = new List<ImageHit>();
        foreach (var r in JsonNode.Parse(json)?["results"] as JsonArray ?? [])
        {
            if (r is not JsonObject o || Str(o, "url") is not { Length: > 0 } image) continue;
            var license = (Str(o, "license") ?? "").ToUpperInvariant() is var l && l is "CC0" or "PDM"
                ? (l == "PDM" ? NxText.T("Domínio público", "Public domain", "Dominio público") : "CC0")
                : $"CC {(Str(o, "license") ?? "").ToUpperInvariant()} {Str(o, "license_version")}".Trim();
            list.Add(new ImageHit("Openverse" + (Str(o, "source") is { Length: > 0 } src ? $" · {src}" : ""),
                Clean(Str(o, "title") ?? "imagem"), Str(o, "thumbnail") ?? image, image, Str(o, "foreign_landing_url") ?? image,
                Clean(Str(o, "creator") ?? ""), license, Str(o, "license_url") ?? "", Int(o, "width"), Int(o, "height")));
        }
        return list;
    }

    /// <summary>Resposta da API do Wikimedia Commons → figuras com licença aceita (público para o teste).</summary>
    public static List<ImageHit> ParseWikimedia(string json, bool allowNc)
    {
        var list = new List<ImageHit>();
        foreach (var (_, page) in JsonNode.Parse(json)?["query"]?["pages"] as JsonObject ?? [])
        {
            if (page?["imageinfo"]?[0] is not JsonObject info || Str(info, "url") is not { Length: > 0 } image) continue;
            var meta = info["extmetadata"];
            string Meta(string key) => meta?[key]?["value"]?.ToString() ?? "";
            var license = Clean(Meta("LicenseShortName"));
            if (!LicenseAllowed(license, allowNc)) continue;
            var title = Clean(Meta("ObjectName")) is { Length: > 0 } obj ? obj
                : Regex.Replace(page["title"]?.ToString() ?? "", @"^File:|\.[A-Za-z0-9]{2,5}$", "");
            list.Add(new ImageHit("Wikimedia Commons", title, Str(info, "thumburl") ?? image, image, Str(info, "descriptionurl") ?? image,
                Clean(Meta("Artist")), license, Meta("LicenseUrl"), Int(info, "width"), Int(info, "height")));
        }
        return list;
    }

    /// <summary>Licença boa para aula: domínio público, CC0, CC BY, CC BY-SA (e NC, se permitido). Fora: sem licença, "fair use", ND não conta como problema.</summary>
    public static bool LicenseAllowed(string license, bool allowNc)
    {
        var l = license.ToUpperInvariant();
        if (l.Length == 0 || l.Contains("FAIR USE") || l.Contains("COPYRIGHTED")) return false;
        if (l.Contains("-NC") || l.Contains(" NC")) return allowNc;
        return l.Contains("PUBLIC DOMAIN") || l.StartsWith("PD") || l.Contains("CC0") || l.StartsWith("CC BY") || l.StartsWith("CC-BY") || l.Contains("ATTRIBUTION");
    }

    /// <summary>HTML de metadados (autor vem com link) → texto simples.</summary>
    private static string Clean(string text) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " ")), @"\s+", " ").Trim();

    private static string Fold(string text)
    {
        var normalized = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString();
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToString();

    private static int Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
}
