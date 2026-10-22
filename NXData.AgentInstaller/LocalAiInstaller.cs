// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace NXData.AgentInstaller;

/// <summary>Situação da IA Local numa pasta.</summary>
public sealed record LocalAiStatus(string Folder, bool BackendPresent, bool ModelPresent, bool ModelValid, string? ModelFile)
{
    public bool Installed => BackendPresent && ModelPresent && ModelValid;
}

/// <summary>Progresso da instalação: etapa e, no download, bytes baixados/total.</summary>
public sealed record LocalAiProgress(string Message, long Downloaded = 0, long Total = 0)
{
    public double Percent => Total > 0 ? Downloaded * 100.0 / Total : 0;
}

/// <summary>Configuração; URLs e versão existem para os testes (o padrão é o do NXProject).</summary>
public sealed record LocalAiInstallOptions
{
    /// <summary>Mesma versão do LLamaSharp do NXPilot e do NXProject: a llama.dll tem que casar com ela.</summary>
    public string BackendVersion { get; init; } = "0.27.0";
    public string BackendPackageUrlFormat { get; init; } = "https://www.nuget.org/api/v2/package/LLamaSharp.Backend.Cpu/{0}";

    /// <summary>Qwen3-4B-Instruct-2507 (Apache-2.0), o modelo padrão do NXProject e do NXPilot.</summary>
    public string ModelFileName { get; init; } = "Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    public string ModelUrl { get; init; } = "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf";

    /// <summary>Espaço livre exigido antes de baixar (modelo ~2,5 GB + backend + folga).</summary>
    public long RequiredFreeBytes { get; init; } = 3L * 1024 * 1024 * 1024;

    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

/// <summary>
/// IA Local (LLaMA) compartilhada pelo NXProject e pelo NXPilot: o backend de CPU do LLamaSharp
/// (llama.dll e as ggml*.dll, extraídas do pacote NuGet) e o modelo GGUF, numa pasta só. Faz o
/// mesmo que o "Gerenciar IA Local" do NXProject — mesma pasta (a do local-ai.json dele, ou
/// C:\IA_LOCAL), mesmos arquivos de registro (backend-version.txt / backend-kind.txt) — para os
/// dois produtos enxergarem a mesma instalação. Sem script: o .NET baixa e extrai.
/// </summary>
public sealed class LocalAiInstaller(LocalAiInstallOptions? options = null, HttpMessageHandler? handler = null)
{
    public const string NativeDllName = "llama.dll";
    public const string DefaultFolder = @"C:\IA_LOCAL";

    private readonly LocalAiInstallOptions _options = options ?? new LocalAiInstallOptions();

    /// <summary>Pasta da IA Local: a configurada no NXProject (local-ai.json) ou C:\IA_LOCAL.</summary>
    public string ResolveFolder()
    {
        try
        {
            var cfg = Path.Combine(_options.LocalAppData, "NXProject.Community", "local-ai.json");
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("ResourcesFolder", out var f) && f.GetString() is { Length: > 0 } folder)
                    return folder;
            }
        }
        catch { /* config inválida: usa o padrão */ }
        return DefaultFolder;
    }

    /// <summary>Checagem leve (sem carregar a DLL): backend presente e um .gguf com a assinatura GGUF.</summary>
    public LocalAiStatus Check(string? folder = null)
    {
        folder ??= ResolveFolder();
        if (!Directory.Exists(folder)) return new LocalAiStatus(folder, false, false, false, null);
        var backend = File.Exists(Path.Combine(folder, NativeDllName));
        var gguf = Directory.EnumerateFiles(folder, "*.gguf").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        return new LocalAiStatus(folder, backend, gguf != null, gguf != null && HasGgufMagic(gguf), gguf == null ? null : Path.GetFileName(gguf));
    }

    /// <summary>
    /// Instala o que falta (backend e/ou modelo). O modelo vai para um .tmp e só vira o .gguf
    /// depois de conferido (tamanho, SHA-256 do Hugging Face quando informado, assinatura GGUF).
    /// </summary>
    public async Task<LocalAiStatus> InstallAsync(string? folder = null, IProgress<LocalAiProgress>? progress = null, CancellationToken ct = default)
    {
        folder ??= ResolveFolder();
        Directory.CreateDirectory(folder);
        var before = Check(folder);
        if (before.Installed) return before;

        var needModel = !before.ModelPresent || !before.ModelValid;
        if (needModel) EnsureFreeSpace(folder);

        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan; // ~2,5 GB: quem limita é o CancellationToken
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NXData.AgentInstaller");

        if (!before.BackendPresent) await InstallBackendAsync(http, folder, progress, ct);
        if (needModel) await InstallModelAsync(http, folder, progress, ct);

        var after = Check(folder);
        progress?.Report(new LocalAiProgress(after.Installed ? "IA Local instalada." : "Instalação incompleta."));
        return after;
    }

    private void EnsureFreeSpace(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < _options.RequiredFreeBytes)
                throw new IOException($"Espaço insuficiente em {root}: {free / (1024 * 1024)} MB livres, são necessários {_options.RequiredFreeBytes / (1024 * 1024)} MB.");
        }
        catch (ArgumentException) { /* unidade de rede/sem letra: segue, o download avisa se faltar */ }
    }

    /// <summary>Pacote NuGet do backend de CPU: extrai as DLLs nativas win-x64 (variante AVX2 por cima da base).</summary>
    private async Task InstallBackendAsync(HttpClient http, string folder, IProgress<LocalAiProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new LocalAiProgress($"Baixando o backend do LLaMA (LLamaSharp {_options.BackendVersion}) do NuGet…"));
        var tmp = Path.Combine(folder, "_backend.nupkg.tmp");
        try
        {
            await DownloadAsync(http, string.Format(_options.BackendPackageUrlFormat, _options.BackendVersion), tmp, null, progress, ct);
            progress?.Report(new LocalAiProgress("Extraindo as DLLs nativas…"));
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var native = zip.Entries.Where(e => Norm(e.FullName).Contains("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase)
                                                    && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
                if (native.Count == 0) throw new InvalidDataException("O pacote do backend não contém DLLs nativas win-x64.");
                var chosen = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in native.Where(e => !Norm(e.FullName)[(Norm(e.FullName).IndexOf("native/", StringComparison.OrdinalIgnoreCase) + 7)..].Contains('/')))
                    chosen[e.Name] = e;                                  // raiz do native: base
                foreach (var e in native.Where(e => Norm(e.FullName).Contains("/avx2/", StringComparison.OrdinalIgnoreCase)))
                    chosen[e.Name] = e;                                  // AVX2 sobrepõe (CPU)
                if (chosen.Count == 0) foreach (var e in native) chosen[e.Name] = e; // layout novo: tudo, sem repetir nome
                foreach (var e in chosen.Values)
                {
                    ct.ThrowIfCancellationRequested();
                    e.ExtractToFile(Path.Combine(folder, e.Name), overwrite: true);
                }
            }
            File.WriteAllText(Path.Combine(folder, "backend-version.txt"), _options.BackendVersion);
            File.WriteAllText(Path.Combine(folder, "backend-kind.txt"), "Cpu");
        }
        finally { TryDelete(tmp); }
        if (!File.Exists(Path.Combine(folder, NativeDllName)))
            throw new InvalidDataException($"{NativeDllName} não veio no pacote do backend.");
    }

    private async Task InstallModelAsync(HttpClient http, string folder, IProgress<LocalAiProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new LocalAiProgress($"Baixando o modelo {_options.ModelFileName} (~2,5 GB) do Hugging Face…"));
        var target = Path.Combine(folder, _options.ModelFileName);
        var tmp = target + ".tmp";
        try
        {
            var published = await PublishedShaAsync(_options.ModelUrl, ct);
            var onDownload = await DownloadAsync(http, _options.ModelUrl, tmp, "x-linked-etag", progress, ct);
            var expectedSha = published ?? onDownload; // servidor sem redirecionamento manda no próprio GET
            progress?.Report(new LocalAiProgress("Conferindo o modelo…"));
            if (!HasGgufMagic(tmp)) throw new InvalidDataException("O arquivo baixado não é um modelo GGUF (download incompleto ou arquivo errado).");
            if (expectedSha != null)
            {
                await using var fs = File.OpenRead(tmp);
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O SHA-256 do modelo não confere com o publicado pelo Hugging Face.");
            }
            File.Move(tmp, target, overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// SHA-256 que o Hugging Face publica (X-Linked-ETag). Ele vem SÓ na resposta de
    /// redirecionamento (302) para a CDN: seguindo o redirecionamento, o cabeçalho se perde e a
    /// conferência seria pulada sem aviso. Por isso um HEAD sem seguir redirecionamento.
    /// </summary>
    private async Task<string?> PublishedShaAsync(string url, CancellationToken ct)
    {
        try
        {
            using var probe = handler == null
                ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
                : new HttpClient(handler, disposeHandler: false);
            probe.Timeout = TimeSpan.FromSeconds(30);
            probe.DefaultRequestHeaders.UserAgent.ParseAdd("NXData.AgentInstaller");
            using var resp = await probe.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            return Sha(resp, "x-linked-etag");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; } // sem o SHA publicado, confere tamanho e assinatura GGUF
    }

    private static string? Sha(HttpResponseMessage response, string header)
    {
        if (!response.Headers.TryGetValues(header, out var values)) return null;
        var v = values.FirstOrDefault()?.Trim('"', 'W', '/', ' ');
        return v is { Length: 64 } && v.All(Uri.IsHexDigit) ? v : null;
    }

    /// <summary>
    /// Baixa para <paramref name="destination"/> conferindo o tamanho. Com <paramref name="shaHeader"/>,
    /// devolve o SHA-256 que o servidor publica nesse cabeçalho (o Hugging Face manda o do arquivo LFS).
    /// </summary>
    private static async Task<string?> DownloadAsync(HttpClient http, string url, string destination, string? shaHeader,
        IProgress<LocalAiProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var sha = shaHeader == null ? null : Sha(response, shaHeader);
        var total = response.Content.Headers.ContentLength ?? 0;
        var name = Path.GetFileName(url);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(destination))
        {
            var buffer = new byte[1024 * 1024];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                progress?.Report(new LocalAiProgress($"Baixando {name}… {read / (1024 * 1024)} MB" + (total > 0 ? $" de {total / (1024 * 1024)} MB" : ""), read, total));
            }
            if (total > 0 && read != total) throw new IOException($"Download incompleto de {name}: {read} de {total} bytes.");
        }
        return sha;
    }

    private static bool HasGgufMagic(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            using var fs = File.OpenRead(path);
            return fs.Read(magic) == 4 && magic[0] == 'G' && magic[1] == 'G' && magic[2] == 'U' && magic[3] == 'F';
        }
        catch { return false; }
    }

    private static string Norm(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Remove a IA Local da pasta (modelo, DLLs do backend e os arquivos de controle). Só apaga o que este
    /// instalador põe lá — outros arquivos da pasta ficam; a pasta só sai se ficar vazia. A pasta é a mesma
    /// do NXProject: removida aqui, some para os dois. Com o NXPilot/NXProject abertos, a DLL está em uso.
    /// </summary>
    public string Uninstall(string? folder = null)
    {
        folder ??= ResolveFolder();
        if (!Directory.Exists(folder)) return folder;
        var files = new[] { "llama*.dll", "ggml*.dll", "mtmd*.dll", "llava*.dll", "*.gguf", "*.gguf.tmp", "_backend.nupkg.tmp" }
            .SelectMany(p => Directory.EnumerateFiles(folder, p))
            .Concat(new[] { "backend-version.txt", "backend-kind.txt" }.Select(f => Path.Combine(folder, f)).Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            foreach (var f in files) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Não foi possível remover a IA Local: algum arquivo está em uso. Feche o NXPilot e o NXProject e tente de novo. ({ex.Message})", ex);
        }
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        return folder;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* .tmp fica para a próxima tentativa sobrescrever */ }
    }
}
