// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Net.Http;

namespace NXData.AgentInstaller;

/// <summary>
/// Modelo ONNX de embeddings (all-MiniLM-L6-v2, ~90 MB) usado pelo 2º degrau da cascata do NXPilot.
/// Fica ao lado da IA Local, em C:\IA_LOCAL\ONNX\all-MiniLM-L6-v2 (model.onnx + vocab.txt), baixado
/// do Hugging Face. Cada arquivo vai para um .part e só é renomeado depois do tamanho conferido.
/// </summary>
public sealed class OnnxModelInstaller(string? root = null, HttpMessageHandler? handler = null,
    string baseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/")
{
    public const string DefaultRoot = @"C:\IA_LOCAL\ONNX";
    public const string ModelName = "all-MiniLM-L6-v2";

    public string Folder { get; } = Path.Combine(root ?? DefaultRoot, ModelName);
    public string ModelPath => Path.Combine(Folder, "model.onnx");
    public string VocabPath => Path.Combine(Folder, "vocab.txt");

    public bool IsInstalled => File.Exists(ModelPath) && File.Exists(VocabPath);

    /// <summary>Remove o modelo e o vocabulário (e a pasta, se ficar vazia). Com o NXPilot aberto, o modelo está em uso.</summary>
    public void Uninstall()
    {
        try
        {
            foreach (var f in new[] { ModelPath, VocabPath, ModelPath + ".part", VocabPath + ".part" })
                if (File.Exists(f)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Não foi possível remover o modelo ONNX: está em uso. Feche o NXPilot e tente de novo. ({ex.Message})", ex);
        }
        if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any()) Directory.Delete(Folder);
    }

    /// <summary>Baixa o que falta (vocab e modelo); o que já existe fica.</summary>
    public async Task InstallAsync(IProgress<LocalAiProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NXData.AgentInstaller");
        await DownloadAsync(http, baseUrl + "vocab.txt", VocabPath, progress, ct);
        await DownloadAsync(http, baseUrl + "onnx/model.onnx", ModelPath, progress, ct);
        progress?.Report(new LocalAiProgress($"Modelo ONNX pronto em {Folder}"));
    }

    private static async Task DownloadAsync(HttpClient http, string url, string target, IProgress<LocalAiProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(target)) return;
        var name = Path.GetFileName(target);
        var part = target + ".part";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buffer = new byte[1 << 16];
            long done = 0, lastMb = -1;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                var mb = done / (1024 * 1024);
                if (mb == lastMb) continue;
                lastMb = mb;
                progress?.Report(new LocalAiProgress($"Baixando {name}: {mb}" + (total > 0 ? $" / {total / (1024 * 1024)} MB" : " MB"), done, total));
            }
            if (total > 0 && done != total) throw new IOException($"Download incompleto de {name}: {done} de {total} bytes.");
        }
        File.Move(part, target, overwrite: true);
    }
}
