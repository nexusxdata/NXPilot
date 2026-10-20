// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NXPilot.Updates;

/// <summary>Versão publicada no GitHub (tag vX.Y.Z.WW) com o pacote do app.</summary>
public sealed record PublishedVersion(string Tag, Version Version, string DownloadUrl, string? Sha256, DateTimeOffset PublishedAt, string HtmlUrl);

/// <summary>Setup (base: runtime + bibliotecas de terceiros) publicado na release fixa.</summary>
public sealed record SetupRelease(string DownloadUrl, DateTimeOffset BuiltAt, string HtmlUrl);

/// <summary>
/// Atualização no modelo do NXProject: cada versão publica só o pacote do app
/// (<see cref="AppAsset"/>, sem runtime) numa release vX.Y.Z.WW; o Setup (a base) vive na
/// release FIXA <see cref="SetupTag"/> e só muda quando a base muda — o carimbo de build
/// (<see cref="SetupStampAsset"/>) diz se mudou. Diferente do NXProject, a troca dos arquivos
/// não usa script: arquivos em uso são RENOMEADOS (*.nxold — o Windows permite renomear exe/dll
/// carregados) e os novos copiados no lugar; os .nxold somem no próximo início.
/// </summary>
public sealed class UpdateService(HttpMessageHandler? handler = null, string repo = "nexusxdata/NXPilot")
{
    public const string AppAsset = "NXPilot-Release.zip";
    public const string SetupAsset = "NXPilot-Setup.zip";
    public const string SetupStampAsset = "NXPilot-Setup.timestamp.txt";
    public const string SetupTag = "nxpilot-setup-latest";
    /// <summary>Gravado pelo Setup na pasta do app: quando a base instalada foi gerada.</summary>
    public const string SetupStampFile = "setup-build-timestamp.txt";
    public const string OldSuffix = ".nxold";

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var c = handler == null
            ? new HttpClient(new HttpClientHandler { DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials })
            : new HttpClient(handler, disposeHandler: false);
        c.Timeout = TimeSpan.FromMinutes(10);
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NXPilot-Updater", "1.0")); // o GitHub exige
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Versão do app que está rodando (AssemblyVersion, ex.: 1.0.0.2).</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    // ================================================================ consultar

    /// <summary>Versões publicadas com o pacote do app, da mais nova para a mais antiga (ignora a release do Setup).</summary>
    public async Task<IReadOnlyList<PublishedVersion>> ListVersionsAsync(int max = 15, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://api.github.com/repos/{repo}/releases?per_page=50", ct);
        var list = new List<PublishedVersion>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (tag == SetupTag || !TryParseTag(tag, out var version)) continue;
            if (Asset(r, AppAsset) is not { } asset) continue; // versões antigas (só com o Setup) não servem para atualizar
            list.Add(new PublishedVersion(tag, version, asset.Url, asset.Sha256,
                DateTimeOffset.Parse(r.GetProperty("published_at").GetString()!, CultureInfo.InvariantCulture),
                r.GetProperty("html_url").GetString() ?? ""));
        }
        return list.OrderByDescending(v => v.Version).Take(max).ToList();
    }

    /// <summary>A versão mais nova, se for maior que <paramref name="current"/>; senão null.</summary>
    public async Task<PublishedVersion?> CheckForUpdateAsync(Version? current = null, CancellationToken ct = default)
    {
        var latest = (await ListVersionsAsync(1, ct)).FirstOrDefault();
        return latest != null && latest.Version > (current ?? CurrentVersion) ? latest : null;
    }

    /// <summary>Setup da release fixa, com o carimbo de build (null se ainda não foi publicado).</summary>
    public async Task<SetupRelease?> GetSetupReleaseAsync(CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = await GetJsonAsync($"https://api.github.com/repos/{repo}/releases/tags/{SetupTag}", ct); }
        catch (UpdateException e) when (e.HttpStatus == 404) { return null; }
        using (doc)
        {
            if (Asset(doc.RootElement, SetupAsset) is not { } zip || Asset(doc.RootElement, SetupStampAsset) is not { } stamp) return null;
            var text = (await GetStringAsync(stamp.Url, ct)).Trim();
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var builtAt)) return null;
            return new SetupRelease(zip.Url, builtAt, doc.RootElement.GetProperty("html_url").GetString() ?? "");
        }
    }

    /// <summary>
    /// A base (Setup) mudou? Sem carimbo instalado (instalação feita por um Setup antigo) conta
    /// como mudou: rodar o Setup uma vez a mais é melhor do que atualizar só o app e faltar uma
    /// biblioteca da base (o app fecharia ao usá-la). Depois do Setup o carimbo existe e a
    /// comparação volta a ser exata.
    /// </summary>
    public static bool SetupChanged(DateTimeOffset? installed, DateTimeOffset published) => installed is not { } i || published > i;

    /// <summary>Carimbo da base instalada (arquivo gravado pelo Setup na pasta do app).</summary>
    public static DateTimeOffset? InstalledSetupStamp(string appDir)
    {
        try
        {
            var file = Path.Combine(appDir, SetupStampFile);
            return File.Exists(file) && DateTimeOffset.TryParse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t) ? t : null;
        }
        catch { return null; }
    }

    // ================================================================ baixar e aplicar

    /// <summary>Baixa o pacote, confere o SHA-256 publicado pelo GitHub (quando houver) e extrai numa pasta temporária.</summary>
    public async Task<string> DownloadAsync(PublishedVersion version, IProgress<string>? status = null, CancellationToken ct = default,
        IProgress<(long Downloaded, long Total)>? bytes = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "nxpilot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, AppAsset);
        status?.Report($"Baixando {version.Tag}…");
        await DownloadFileAsync(version.DownloadUrl, zip, bytes, ct);
        if (version.Sha256 != null)
        {
            await using var fs = File.OpenRead(zip);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(version.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException($"O pacote {version.Tag} não confere com o SHA-256 publicado (download corrompido ou adulterado). Nada foi alterado.");
        }
        var extracted = Path.Combine(temp, "app");
        ZipFile.ExtractToDirectory(zip, extracted);
        if (!File.Exists(Path.Combine(extracted, "NxPilot.exe")))
            throw new UpdateException($"O pacote {version.Tag} não tem o NxPilot.exe. Nada foi alterado.");
        return extracted;
    }

    /// <summary>
    /// Copia o pacote sobre a pasta do app SEM script: o arquivo existente (mesmo em uso pelo
    /// próprio app) é renomeado para *.nxold e o novo é copiado no lugar. Se algo falhar no meio,
    /// desfaz o que já trocou. Devolve quantos arquivos foram atualizados.
    /// </summary>
    public static int Apply(string packageDir, string appDir)
    {
        var done = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var source in Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(appDir, Path.GetRelativePath(packageDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + OldSuffix;
                    if (File.Exists(backup)) File.Delete(backup); // sobra de uma atualização anterior
                    File.Move(target, backup);
                }
                done.Add((target, backup));
                File.Copy(source, target);
            }
            return done.Count;
        }
        catch
        {
            foreach (var (target, backup) in Enumerable.Reverse(done))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (backup != null && File.Exists(backup)) File.Move(backup, target);
                }
                catch { /* melhor esforço: o .nxold continua lá para recuperação manual */ }
            }
            throw;
        }
    }

    /// <summary>No início do app: apaga os *.nxold da atualização anterior (os que ainda estiverem em uso ficam para depois).</summary>
    public static int CleanupOldFiles(string appDir)
    {
        var removed = 0;
        try
        {
            foreach (var f in Directory.GetFiles(appDir, "*" + OldSuffix, SearchOption.AllDirectories))
                try { File.Delete(f); removed++; } catch { /* ainda em uso */ }
        }
        catch { /* pasta sem permissão */ }
        return removed;
    }

    public Task DownloadFileAsync(string url, string destPath, CancellationToken ct = default) => DownloadFileAsync(url, destPath, null, ct);

    /// <summary>Baixa com progresso em bytes (baixado, total) — a tela mostra MB e % e, no fim, 100%.</summary>
    public async Task DownloadFileAsync(string url, string destPath, IProgress<(long Downloaded, long Total)>? bytes, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"HTTP {(int)resp.StatusCode} ao baixar {url}", (int)resp.StatusCode);
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);
        var buffer = new byte[1 << 16];
        long done = 0, lastReported = -1;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (bytes != null && done - lastReported >= 256 * 1024) { bytes.Report((done, total)); lastReported = done; } // a cada ~256 KB
        }
        bytes?.Report((done, total > 0 ? total : done)); // fim: sempre 100%
    }

    /// <summary>"45,2 MB de 93,4 MB (48%)" — o mesmo texto do NXProject; no fim, "100% · download concluído".</summary>
    public static string DescribeProgress(long downloaded, long total) =>
        total > 0 && downloaded >= total
            ? NxText.T($"100% · download concluído ({total / 1048576d:0.0} MB)", $"100% · download complete ({total / 1048576d:0.0} MB)", $"100% · descarga concluida ({total / 1048576d:0.0} MB)")
            : total > 0
                ? NxText.T($"{downloaded / 1048576d:0.0} MB de {total / 1048576d:0.0} MB ({downloaded * 100 / total}%)", $"{downloaded / 1048576d:0.0} MB of {total / 1048576d:0.0} MB ({downloaded * 100 / total}%)", $"{downloaded / 1048576d:0.0} MB de {total / 1048576d:0.0} MB ({downloaded * 100 / total}%)")
                : $"{downloaded / 1048576d:0.0} MB";

    // ---------------------------------------------------------------- apoio

    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0);
        return tag.Length > 1 && (tag[0] is 'v' or 'V') && Version.TryParse(tag[1..], out version!);
    }

    private static (string Url, string? Sha256)? Asset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
            if (a.GetProperty("name").GetString() == name)
            {
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null; // "sha256:…" (GitHub)
                return (a.GetProperty("browser_download_url").GetString()!,
                        digest?.StartsWith("sha256:") == true ? digest["sha256:".Length..] : null);
            }
        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct) => JsonDocument.Parse(await GetStringAsync(url, ct));

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(url, ct); }
        catch (HttpRequestException e) { throw new UpdateException("Sem conexão com o GitHub: " + e.Message); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw new UpdateException($"GitHub HTTP {(int)resp.StatusCode} em {url}", (int)resp.StatusCode);
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}

public sealed class UpdateException(string message, int? httpStatus = null) : Exception(message)
{
    public int? HttpStatus { get; } = httpStatus;
}
