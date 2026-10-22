// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NXData.AgentInstaller;

public interface IAgentInstaller
{
    /// <summary>Versão que será instalada (a testada, ou a última se a testada não existir mais).</summary>
    Task<(string Version, bool FellBackToLatest)> ResolveVersionAsync(AgentCli cli, VersionChoice choice, CancellationToken ct = default);

    Task<AgentInstallResult> InstallAsync(AgentCli cli, InstallMode mode, VersionChoice choice,
        IProgress<string>? status = null, CancellationToken ct = default);

    /// <summary>Comando oficial (PowerShell) — para mostrar/copiar ou rodar no modo Script.</summary>
    string ScriptCommand(AgentCli cli, string? version = null);
}

/// <summary>
/// Instala o Claude Code e o Codex CLI no Windows do mesmo jeito que os instaladores oficiais
/// (mesmas pastas, mesmo checksum), mas, no modo Interno, sem rodar nenhum script:
/// <list type="bullet">
/// <item>Claude Code: versão em …/latest, SHA-256 do manifest.json, download do claude.exe e
/// "claude.exe install &lt;versão&gt;" — o próprio binário oficial monta ~/.local/bin.</item>
/// <item>Codex: release.json (releases.openai.com, com queda para o GitHub), SHA-256 do
/// codex-package_SHA256SUMS, extração do pacote completo (codex.exe + sandbox + rg) em
/// ~/.codex/packages/standalone e junções current/bin, igual ao install.ps1 oficial.</item>
/// </list>
/// </summary>
public sealed class AgentInstaller(AgentInstallOptions? options = null, HttpMessageHandler? handler = null) : IAgentInstaller
{
    private readonly AgentInstallOptions _o = options ?? new AgentInstallOptions();
    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var c = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        c.Timeout = TimeSpan.FromMinutes(15); // o pacote do Codex passa de 150 MB
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NXData.AgentInstaller/1.0"); // o GitHub exige
        return c;
    }

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$", RegexOptions.Compiled);
    private static bool Arm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;
    private static string ClaudePlatform => Arm64 ? "win32-arm64" : "win32-x64";
    private static string CodexTarget => Arm64 ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";

    // ================================================================ versão

    public async Task<(string Version, bool FellBackToLatest)> ResolveVersionAsync(AgentCli cli, VersionChoice choice, CancellationToken ct = default)
    {
        if (choice == VersionChoice.Tested)
        {
            var tested = cli == AgentCli.ClaudeCode ? _o.TestedClaudeVersion : _o.TestedCodexVersion;
            var available = cli == AgentCli.ClaudeCode
                ? await ExistsAsync($"{_o.ClaudeDownloadBase}/{tested}/manifest.json", ct)
                : await ExistsAsync($"{_o.CodexReleasesBase}/releases/{tested}/release.json", ct)
                  || await ExistsAsync($"{_o.CodexGitHubApi}/tags/rust-v{tested}", ct);
            if (available) return (tested, false);
        }
        return (await LatestAsync(cli, ct), choice == VersionChoice.Tested);
    }

    private async Task<string> LatestAsync(AgentCli cli, CancellationToken ct)
    {
        if (cli == AgentCli.ClaudeCode)
        {
            var v = (await GetStringAsync($"{_o.ClaudeDownloadBase}/latest", ct)).Trim();
            // HTML de erro/bloqueio regional não pode virar "versão".
            if (!VersionPattern.IsMatch(v))
                throw new AgentInstallException("downloads.claude.ai não devolveu uma versão válida (serviço fora do ar ou região não suportada).");
            return v;
        }
        using var doc = await CodexLatestMetadataAsync(ct);
        return NormalizeCodexTag(doc.RootElement.GetProperty("tag_name").GetString());
    }

    private async Task<JsonDocument> CodexLatestMetadataAsync(CancellationToken ct)
    {
        try { return JsonDocument.Parse(await GetStringAsync($"{_o.CodexReleasesBase}/channels/latest", ct)); }
        catch (Exception e) when (e is HttpRequestException or JsonException or AgentInstallException)
        { return JsonDocument.Parse(await GetStringAsync($"{_o.CodexGitHubApi}/latest", ct)); }
    }

    private static string NormalizeCodexTag(string? tag)
    {
        var v = (tag ?? "").Trim();
        if (v.StartsWith("rust-v")) v = v["rust-v".Length..];
        else if (v.StartsWith('v')) v = v[1..];
        if (!VersionPattern.IsMatch(v)) throw new AgentInstallException($"Versão do Codex inválida: '{tag}'.");
        return v;
    }

    // ================================================================ instalar

    public async Task<AgentInstallResult> InstallAsync(AgentCli cli, InstallMode mode, VersionChoice choice,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var name = cli == AgentCli.ClaudeCode ? "Claude Code" : "Codex";
        status?.Report($"{name}: verificando a versão…");
        var (version, fellBack) = await ResolveVersionAsync(cli, choice, ct);
        var fallbackNote = fellBack
            ? $"A versão testada ({(cli == AgentCli.ClaudeCode ? _o.TestedClaudeVersion : _o.TestedCodexVersion)}) não está mais disponível; instalada a última ({version}). "
            : "";

        if (mode == InstallMode.Script)
        {
            status?.Report($"{name} {version}: abrindo o instalador oficial…");
            var exit = await RunScriptWindowAsync(ScriptCommand(cli, choice == VersionChoice.Tested && !fellBack ? version : null), ct);
            var found = AgentLocator.Find(cli, _o);
            if (exit != 0 || found == null)
                throw new AgentInstallException($"O instalador oficial do {name} terminou com código {exit}{(found == null ? " e o agente não foi encontrado" : "")}. " +
                                                "Se o antivírus bloqueou o script, use a instalação interna.");
            return new(cli, found.Version ?? version, found.Path, mode, fellBack, fallbackNote + $"{name} instalado pelo script oficial.");
        }

        var path = cli == AgentCli.ClaudeCode
            ? await InstallClaudeAsync(version, choice == VersionChoice.Latest || fellBack ? "latest" : version, status, ct)
            : await InstallCodexAsync(version, choice == VersionChoice.Latest || fellBack, status, ct);
        return new(cli, version, path, mode, fellBack, fallbackNote + $"{name} {version} instalado.");
    }

    // ---------------------------------------------------------------- Claude Code

    /// <param name="target">"latest" (continua se atualizando) ou a versão fixa — igual ao parâmetro do install.ps1.</param>
    private async Task<string> InstallClaudeAsync(string version, string target, IProgress<string>? status, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await GetStringAsync($"{_o.ClaudeDownloadBase}/{version}/manifest.json", ct));
        if (!manifest.RootElement.GetProperty("platforms").TryGetProperty(ClaudePlatform, out var platform) ||
            platform.GetProperty("checksum").GetString() is not { Length: 64 } checksum)
            throw new AgentInstallException($"O Claude Code {version} não tem binário para {ClaudePlatform}.");

        var downloads = Path.Combine(_o.UserProfile, ".claude", "downloads");
        Directory.CreateDirectory(downloads);
        var exe = Path.Combine(downloads, $"claude-{version}-{ClaudePlatform}.exe");
        try
        {
            status?.Report($"Claude Code {version}: baixando…");
            await DownloadAsync($"{_o.ClaudeDownloadBase}/{version}/{ClaudePlatform}/claude.exe", exe, ct);
            status?.Report($"Claude Code {version}: conferindo o SHA-256…");
            await VerifySha256Async(exe, checksum, "Claude Code", ct);
            ValidateExe(exe, "Claude Code");

            // Passo oficial: o próprio claude.exe instala o lançador em ~/.local/bin e a integração.
            status?.Report($"Claude Code {version}: instalando…");
            var run = _o.ClaudeSelfInstall ?? RunProcessAsync;
            var (code, output) = await run(exe, target, ct);
            if (code != 0) throw new AgentInstallException($"claude install terminou com código {code}: {Tail(output)}");
        }
        finally { TryDelete(exe); }

        var installed = Path.Combine(_o.ClaudeLocalBin, "claude.exe");
        if (_o.UpdateUserPath) EnsureOnUserPath(_o.ClaudeLocalBin, prepend: false);
        status?.Report($"Claude Code {version} instalado.");
        return installed;
    }

    private static async Task<(int, string)> RunProcessAsync(string exe, string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi) ?? throw new AgentInstallException("Não foi possível iniciar o claude.exe baixado.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new AgentInstallException("claude install passou de 5 minutos."); }
        return (p.ExitCode, await stdout + await stderr);
    }

    // ---------------------------------------------------------------- Codex

    private static readonly string[] CodexPackageFiles =
        { "codex-package.json", @"bin\codex.exe", @"bin\codex-code-mode-host.exe", @"codex-path\rg.exe",
          @"codex-resources\codex-command-runner.exe", @"codex-resources\codex-windows-sandbox-setup.exe" };

    private async Task<string> InstallCodexAsync(string version, bool trackLatest, IProgress<string>? status, CancellationToken ct)
    {
        var package = $"codex-package-{CodexTarget}.tar.gz";
        const string sums = "codex-package_SHA256SUMS";
        var github = $"https://github.com/openai/codex/releases/download/rust-v{version}";
        var openai = $"{_o.CodexReleasesBase}/releases/{version}";

        var temp = Path.Combine(Path.GetTempPath(), "nx-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var root = _o.CodexStandaloneRoot;
        var releases = Path.Combine(root, "releases");
        var releaseName = $"{version}-{CodexTarget}";
        var releaseDir = Path.Combine(releases, releaseName);
        try
        {
            if (!CodexPackageFiles.All(f => File.Exists(Path.Combine(releaseDir, f))))
            {
                status?.Report($"Codex {version}: baixando a lista de checksums…");
                var sumsPath = Path.Combine(temp, sums);
                await DownloadWithFallbackAsync($"{openai}/{sums}", $"{github}/{sums}", sumsPath, ct);
                var expected = File.ReadLines(sumsPath)
                    .Select(l => Regex.Match(l, @"^\s*([0-9a-fA-F]{64})\s+(\S+)\s*$"))
                    .Where(m => m.Success && m.Groups[2].Value == package)
                    .Select(m => m.Groups[1].Value.ToLowerInvariant()).FirstOrDefault()
                    ?? throw new AgentInstallException($"{package} não está no {sums} do Codex {version}.");

                status?.Report($"Codex {version}: baixando o pacote (~160 MB)…");
                var archive = Path.Combine(temp, package);
                await DownloadWithFallbackAsync($"{openai}/{package}", $"{github}/{package}", archive, ct);
                status?.Report($"Codex {version}: conferindo o SHA-256…");
                await VerifySha256Async(archive, expected, "Codex", ct);

                status?.Report($"Codex {version}: extraindo…");
                var staging = Path.Combine(releases, $".staging.{releaseName}.{Environment.ProcessId}");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                await using (var file = File.OpenRead(archive))
                await using (var gz = new GZipStream(file, CompressionMode.Decompress))
                    await TarFile.ExtractToDirectoryAsync(gz, staging, overwriteFiles: true, ct);
                var missing = CodexPackageFiles.Where(f => !File.Exists(Path.Combine(staging, f))).ToList();
                if (missing.Count > 0)
                    throw new AgentInstallException($"O pacote do Codex {version} veio sem: {string.Join(", ", missing)} (o formato pode ter mudado; tente o modo Script).");
                if (Directory.Exists(releaseDir)) Directory.Delete(releaseDir, true);
                Directory.Move(staging, releaseDir);
            }

            // Mesma estrutura do install.ps1: current → release; …\OpenAI\Codex\bin → current\bin.
            var current = Path.Combine(root, "current");
            Junction(current, releaseDir);
            var marker = Path.Combine(root, "auto-update-version");
            if (trackLatest) File.WriteAllText(marker, releaseName); // o Codex continua se atualizando sozinho
            else TryDelete(marker);                                  // versão fixa: não troca sozinho
            Junction(_o.CodexVisibleBin, Path.Combine(current, "bin"));
            if (_o.UpdateUserPath) EnsureOnUserPath(_o.CodexVisibleBin, prepend: true);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }

        status?.Report($"Codex {version} instalado.");
        return Path.Combine(_o.CodexVisibleBin, "codex.exe");
    }

    /// <summary>Junção de diretório (não exige admin). Junção antiga é trocada; pasta real é guardada como .old.</summary>
    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new DirectoryInfo(link);
        if (info.Exists)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete();             // só o link
            else Directory.Move(link, $"{link}.old-{DateTime.Now:yyyyMMddHHmmss}");             // nunca apaga pasta real
        }
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0 || !Directory.Exists(link))
            throw new AgentInstallException($"Não foi possível criar a junção {link} → {target}: {err.Trim()}");
    }

    // ---------------------------------------------------------------- Remover

    /// <summary>
    /// Só o que está nas pastas do instalador oficial (onde este pacote também instala) pode ser removido aqui.
    /// Cópia do npm/PATH, a pasta antiga do NXProject e o binário da extensão do VS Code não são nossos.
    /// </summary>
    public bool CanUninstall(AgentCli cli) => File.Exists(OfficialExe(cli));

    private string OfficialExe(AgentCli cli) => cli == AgentCli.ClaudeCode
        ? Path.Combine(_o.ClaudeLocalBin, "claude.exe")
        : Path.Combine(_o.CodexVisibleBin, "codex.exe");

    /// <summary>
    /// Remove o agente das pastas oficiais (direito de quem instalou). O login e as configurações da pessoa
    /// (~/.claude, ~/.codex) ficam — reinstalar volta como estava. Em uso (agente aberto) dá erro claro.
    /// </summary>
    public Task<string> UninstallAsync(AgentCli cli, CancellationToken ct = default) => Task.Run(() =>
    {
        var name = cli == AgentCli.ClaudeCode ? "Claude Code" : "Codex";
        if (!CanUninstall(cli)) throw new AgentInstallException($"{name} não está nas pastas do instalador oficial — nada a remover aqui.");
        try
        {
            if (cli == AgentCli.ClaudeCode)
            {
                File.Delete(OfficialExe(cli));                                                  // lançador em ~/.local/bin
                DeleteDir(Path.Combine(_o.UserProfile, ".local", "share", "claude"));            // versões baixadas
                if (Directory.Exists(_o.ClaudeLocalBin) && !Directory.EnumerateFileSystemEntries(_o.ClaudeLocalBin).Any())
                {
                    Directory.Delete(_o.ClaudeLocalBin);
                    if (_o.UpdateUserPath) RemoveFromUserPath(_o.ClaudeLocalBin);               // ~/.local/bin pode ser de outros programas
                }
            }
            else
            {
                DeleteDir(_o.CodexVisibleBin);                                                   // junção (ou pasta) …\OpenAI\Codexin
                DeleteDir(_o.CodexStandaloneRoot);                                               // pacotes em ~/.codex/packages/standalone
                foreach (var dir in new[] { Path.GetDirectoryName(_o.CodexVisibleBin)!, Path.GetDirectoryName(Path.GetDirectoryName(_o.CodexVisibleBin)!)! })
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                if (_o.UpdateUserPath) RemoveFromUserPath(_o.CodexVisibleBin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentInstallException($"Não foi possível remover o {name}: algum arquivo está em uso. Feche o NXPilot, o NXProject, o VS Code e os terminais que usam o {name} e tente de novo. ({ex.Message})", ex);
        }
        return $"{name} removido. Seu login e suas configurações foram mantidos (reinstalar volta como estava).";
    }, ct);

    /// <summary>
    /// Junção: apaga só o link (nunca o destino). Pasta real: primeiro as junções de dentro (ex.: "current"
    /// do Codex — apagar recursivo por cima delas dá "acesso negado"), depois o resto, sem atributo somente leitura.
    /// </summary>
    private static void DeleteDir(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); return; }
        foreach (var sub in info.EnumerateDirectories())
            DeleteDir(sub.FullName);
        foreach (var file in info.EnumerateFiles())
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }
        info.Delete();
    }

    /// <summary>Tira a pasta do PATH do usuário (HKCU).</summary>
    public static void RemoveFromUserPath(string dir)
    {
        var cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        var kept = parts.Where(p => !string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count != parts.Count) Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.User);
    }

    // ---------------------------------------------------------------- Script

    public string ScriptCommand(AgentCli cli, string? version = null) => cli switch
    {
        AgentCli.ClaudeCode when version != null => $"& ([scriptblock]::Create((irm https://claude.ai/install.ps1))) {version}",
        AgentCli.ClaudeCode => "irm https://claude.ai/install.ps1 | iex",
        AgentCli.Codex when version != null => $"$env:CODEX_RELEASE='{version}'; irm https://chatgpt.com/codex/install.ps1 | iex",
        _ => "irm https://chatgpt.com/codex/install.ps1 | iex",
    };

    /// <summary>Abre o PowerShell VISÍVEL (a pessoa vê o que o instalador oficial faz) e espera fechar.</summary>
    private static async Task<int> RunScriptWindowAsync(string command, CancellationToken ct)
    {
        var script = command + "; $code = $LASTEXITCODE; if ($null -eq $code) { $code = 0 }; " +
                     "Write-Host ''; Read-Host 'Pressione Enter para fechar'; exit $code";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new AgentInstallException("Não foi possível abrir o PowerShell.");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    // ---------------------------------------------------------------- apoio

    /// <summary>Põe a pasta no PATH do usuário (HKCU, sem admin); prepend quando precisa ganhar de outra instalação.</summary>
    public static void EnsureOnUserPath(string dir, bool prepend)
    {
        var cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = cur.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        var norm = dir.TrimEnd('\\');
        var idx = parts.FindIndex(p => string.Equals(p.TrimEnd('\\'), norm, StringComparison.OrdinalIgnoreCase));
        if (idx == 0 || (idx > 0 && !prepend)) return;
        if (idx > 0) parts.RemoveAt(idx);
        if (prepend) parts.Insert(0, dir); else parts.Add(dir);
        Environment.SetEnvironmentVariable("PATH", string.Join(';', parts), EnvironmentVariableTarget.User);
    }

    private async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new AgentInstallException($"HTTP {(int)resp.StatusCode} em {url}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new AgentInstallException($"HTTP {(int)resp.StatusCode} ao baixar {url}");
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        await src.CopyToAsync(file, ct);
    }

    private async Task DownloadWithFallbackAsync(string url, string fallback, string dest, CancellationToken ct)
    {
        try { await DownloadAsync(url, dest, ct); }
        catch (Exception e) when (e is AgentInstallException or HttpRequestException) { await DownloadAsync(fallback, dest, ct); }
    }

    private static async Task VerifySha256Async(string path, string expected, string what, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new AgentInstallException($"Checksum do {what} não confere (download corrompido ou adulterado). Nada foi instalado.");
    }

    /// <summary>Executável Windows de verdade ("MZ"), não uma página de erro salva como .exe.</summary>
    private static void ValidateExe(string path, string what)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 1024 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            throw new AgentInstallException($"O download do {what} não é um executável válido.");
    }

    private static string Tail(string s) => s.Length > 600 ? "…" + s[^600..] : s.Trim();
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
