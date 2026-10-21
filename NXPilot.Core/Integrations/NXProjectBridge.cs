// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NXPilot.Integrations;

/// <summary>Situação do NXProject instalado para a extensão NXProject.</summary>
/// <param name="Available">DLL achada, carregada e com o contrato esperado.</param>
/// <param name="Message">O que mostrar quando não está disponível (não instalado, versão antiga, atualizando…).</param>
public sealed record NXProjectInfo(bool Available, string? Folder, string? Version, int? Contract, string Message);

/// <summary>
/// Ponte para o NXProject INSTALADO: quem sabe gravar no DevOps/GitProject (campos HH, padrões,
/// regra Adm_NX) é ele — o NXPilot só chama a <c>NXProject.Agent.NxAgentApi</c> do
/// <c>NXProject.Shared.dll</c> da pasta instalada.
/// <list type="bullet">
/// <item>Carrega pela MEMÓRIA (bytes), num contexto descartável: o arquivo não fica preso e o
/// NXProject atualiza com o NXPilot aberto. Mudou o arquivo (data/tamanho) → descarta e recarrega.</item>
/// <item>Confere o nome do assembly e a versão do contrato nos MESMOS bytes que carrega.</item>
/// <item>Nada aqui derruba o NXPilot: sem NXProject, versão antiga ou arquivo em atualização, a
/// extensão só fica indisponível, com o motivo.</item>
/// <item>O PAT/token vai cifrado (DPAPI do usuário + entropia do par); nunca em texto.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public static class NXProjectBridge
{
    public const int ExpectedContract = 1;
    private const string AssemblyFile = "NXProject.Shared.dll";
    private const string ApiType = "NXProject.Agent.NxAgentApi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NXPilot<->NXProject/NxAgentApi/v1");

    private static readonly object Gate = new();
    private static AssemblyLoadContext? _context;
    private static Type? _api;
    private static string? _loadedKey;
    private static NXProjectInfo? _info;

    /// <summary>Pasta do NXProject instalado (a mesma do NXProject-Setup); a variável NXPILOT_NXPROJECT_DIR troca (testes, build local).</summary>
    public static string? FindFolder()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("NXPILOT_NXPROJECT_DIR") is { Length: > 0 } custom) candidates.Add(custom);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apps", "NXProject"));
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, AssemblyFile)));
    }

    /// <summary>Situação atual (carrega ou recarrega se o NXProject mudou). Nunca lança.</summary>
    public static NXProjectInfo Info()
    {
        try { return Load().Info; }
        catch (Exception ex) { return new NXProjectInfo(false, null, null, null, NxText.T($"NXProject indisponível: {ex.Message}", $"NXProject unavailable: {ex.Message}", $"NXProject no disponible: {ex.Message}")); }
    }

    /// <summary>Chama <c>NxAgentApi.StatusAsync</c>. Devolve o JSON de resposta (ok=false com error em qualquer falha).</summary>
    public static Task<JsonObject> StatusAsync(JsonObject request, CancellationToken ct = default) => CallAsync("StatusAsync", request, ct);

    /// <summary>Chama <c>NxAgentApi.CreateItemsAsync</c> (cria filhos; o Adm_NX restrito é conferido lá).</summary>
    public static Task<JsonObject> CreateItemsAsync(JsonObject request, CancellationToken ct = default) => CallAsync("CreateItemsAsync", request, ct);

    /// <summary>Conexão DevOps para a API: org, project e o PAT CIFRADO com a entropia do par.</summary>
    public static JsonObject DevOpsConnection(DevOpsConnection c) => new()
    {
        ["backend"] = "devops", ["org"] = c.OrganizationUrl, ["project"] = c.Project, ["pat"] = Protect(c.Pat),
    };

    /// <summary>Conexão GitProject para a API: dono, número e o token CIFRADO.</summary>
    public static JsonObject GitProjectConnection(GitHubProjectConnection c) => new()
    {
        ["backend"] = "gitproject", ["owner"] = c.Owner, ["ownerIsOrganization"] = c.OwnerIsOrganization, ["number"] = c.Number, ["token"] = Protect(c.Token),
    };

    /// <summary>"••••••••3f9a" — o suficiente para reconhecer qual é, sem expor.</summary>
    public static string Mask(string secret) => secret.Length == 0 ? "" : "••••••••" + (secret.Length > 4 ? secret[^4..] : "");

    private static string Protect(string secret) => secret.Length == 0 ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    private static async Task<JsonObject> CallAsync(string method, JsonObject request, CancellationToken ct)
    {
        try
        {
            var (info, api) = Load();
            if (!info.Available || api == null) return Fail(info.Message);
            var m = api.GetMethod(method, BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)])
                    ?? throw new MissingMethodException(ApiType, method);
            var task = (Task<string>)m.Invoke(null, [request.ToJsonString(), ct])!;
            return JsonNode.Parse(await task) as JsonObject ?? Fail("resposta vazia do NXProject");
        }
        catch (Exception ex) { return Fail((ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message); }
    }

    private static JsonObject Fail(string message) => new() { ["ok"] = false, ["error"] = message };

    /// <summary>Carrega pela memória; recarrega quando o arquivo muda (NXProject atualizado).</summary>
    private static (NXProjectInfo Info, Type? Api) Load()
    {
        lock (Gate)
        {
            var folder = FindFolder();
            if (folder == null)
            {
                Unload();
                return (_info = new NXProjectInfo(false, null, null, null, NxText.T(
                    "NXProject não instalado (procurado em %USERPROFILE%\\Apps\\NXProject). Instale ou atualize o NXProject para usar esta extensão.",
                    "NXProject not installed (looked in %USERPROFILE%\\Apps\\NXProject). Install or update NXProject to use this extension.",
                    "NXProject no instalado (buscado en %USERPROFILE%\\Apps\\NXProject). Instale o actualice NXProject para usar esta extensión.")), null);
            }
            var dll = Path.Combine(folder, AssemblyFile);
            var file = new FileInfo(dll);
            var key = $"{dll}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
            if (key == _loadedKey && _info != null) return (_info, _api);

            Unload();
            var version = FileVersion(Path.Combine(folder, "NXProject.Community.exe")) ?? FileVersion(dll);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(dll); }
            catch (IOException)
            {
                return (_info = new NXProjectInfo(false, folder, version, null, NxText.T(
                    "O NXProject está sendo atualizado agora; tente de novo em instantes.", "NXProject is being updated right now; try again shortly.", "NXProject se está actualizando ahora; intente de nuevo en un momento.")), null);
            }

            var context = new AssemblyLoadContext("NXProject-" + Guid.NewGuid().ToString("N")[..8], isCollectible: true);
            var assembly = context.LoadFromStream(new MemoryStream(bytes));
            if (assembly.GetName().Name != "NXProject.Shared")
            {
                context.Unload();
                return (_info = new NXProjectInfo(false, folder, version, null, $"{AssemblyFile} não é o do NXProject."), null);
            }
            var api = assembly.GetType(ApiType);
            var contract = api?.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as int?;
            if (api == null || contract != ExpectedContract)
            {
                context.Unload();
                return (_info = new NXProjectInfo(false, folder, version, contract, NxText.T(
                    $"O NXProject {version} não tem a API para agentes (contrato {ExpectedContract}). Atualize o NXProject.",
                    $"NXProject {version} lacks the agent API (contract {ExpectedContract}). Update NXProject.",
                    $"NXProject {version} no tiene la API para agentes (contrato {ExpectedContract}). Actualice NXProject.")), null);
            }
            _context = context;
            _api = api;
            _loadedKey = key;
            return (_info = new NXProjectInfo(true, folder, version, contract, NxText.T($"NXProject {version} · contrato {contract}", $"NXProject {version} · contract {contract}", $"NXProject {version} · contrato {contract}")), api);
        }
    }

    private static void Unload()
    {
        _api = null;
        _loadedKey = null;
        _info = null;
        try { _context?.Unload(); } catch { /* já descarregado */ }
        _context = null;
    }

    private static string? FileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null; }
        catch { return null; }
    }
}
