// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NXPilot.Integrations;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Figuras escolhidas para slides: baixa (ou copia da pasta de imagens), reduz para tamanho de slide
/// (lado maior até 1600 px, JPEG 85 — PNG pequeno com transparência fica PNG) e registra o crédito
/// em imagens/CREDITOS.md, para a apresentação não ficar pesada e a licença ser respeitada.
/// </summary>
public static class SlideImages
{
    private const int MaxSide = 1600;
    private const long MaxDownload = 25L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NXPilot/1.0 (+https://github.com/nexusxdata/NXPilot)");
        return http;
    }

    /// <summary>Salva a figura em <paramref name="folder"/> e devolve o caminho do arquivo.</summary>
    public static async Task<string> SaveAsync(ImageHit hit, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        byte[] bytes;
        if (hit.LocalPath != null) bytes = await File.ReadAllBytesAsync(hit.LocalPath, ct);
        else
        {
            using var resp = await Http.GetAsync(hit.ImageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is > MaxDownload) throw new InvalidOperationException("imagem grande demais (> 25 MB)");
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }

        var name = Slug(hit.Title);
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var keepPng = decoder.CodecInfo?.FileExtensions?.Contains(".png", StringComparison.OrdinalIgnoreCase) == true
                      && frame.Format.Masks.Count == 4 && bytes.Length < 1_500_000; // PNG com transparência e leve: fica como está
        if (keepPng)
        {
            var png = Unique(folder, name, ".png");
            await File.WriteAllBytesAsync(png, bytes, ct);
            return png;
        }
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        // JPEG não tem transparência: fundo branco atrás da figura.
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(rgb));
        var jpg = Unique(folder, name, ".jpg");
        await using (var fs = File.Create(jpg)) encoder.Save(fs);
        return jpg;
    }

    /// <summary>Acrescenta a linha de crédito em imagens/CREDITOS.md (arquivo → crédito, licença e link).</summary>
    public static void AppendCredit(string folder, string fileName, ImageHit hit)
    {
        var path = Path.Combine(folder, "CREDITOS.md");
        var sb = new StringBuilder();
        if (!File.Exists(path)) sb.AppendLine(NxText.T("# Créditos das imagens", "# Image credits", "# Créditos de las imágenes")).AppendLine();
        sb.AppendLine($"- **{fileName}** — {hit.Credit}" + (hit.LicenseUrl.Length > 0 ? $" — {hit.LicenseUrl}" : ""));
        File.AppendAllText(path, sb.ToString());
    }

    private static string Slug(string title)
    {
        var folded = new string(title.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = Regex.Replace(folded.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "imagem" : slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    private static string Unique(string folder, string name, string ext)
    {
        var path = Path.Combine(folder, name + ext);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}-{i}{ext}");
        return path;
    }
}
