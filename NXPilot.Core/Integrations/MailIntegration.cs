// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.Ai;
using NXPilot.Mail;

namespace NXPilot.Integrations;

/// <summary>
/// Aplicativos OAuth de e-mail (Entra ID e Google Cloud) em %LOCALAPPDATA%\NXPilot\mail.json.
/// O client secret do Gmail (app desktop) fica cifrado com DPAPI mesmo não sendo confidencial.
/// </summary>
public sealed class MailSettings
{
    public string OutlookClientId { get; set; } = "";
    public string OutlookTenant { get; set; } = "common";
    public string GmailClientId { get; set; } = "";
    [JsonIgnore] public string GmailClientSecret { get; set; } = "";
    public string GmailClientSecretProtected { get; set; } = "";

    public OAuthClientConfig? Config(MailProvider provider) => provider switch
    {
        MailProvider.Outlook when OutlookClientId.Length > 0 => OAuthClientConfig.Outlook(OutlookClientId, OutlookTenant.Length > 0 ? OutlookTenant : "common"),
        MailProvider.Gmail when GmailClientId.Length > 0 && GmailClientSecret.Length > 0 => OAuthClientConfig.Gmail(GmailClientId, GmailClientSecret),
        _ => null,
    };

    internal static string Folder => ExtensionSettings.Folder;
    private static string FilePath => ExtensionSettings.File("mail.json");

    [SupportedOSPlatform("windows")]
    public static MailSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(FilePath)) is { } s)
            {
                s.GmailClientSecret = Secret.Unprotect(s.GmailClientSecretProtected);
                return s;
            }
        }
        catch { /* arquivo inválido */ }
        return new MailSettings();
    }

    [SupportedOSPlatform("windows")]
    public void Save()
    {
        GmailClientSecretProtected = Secret.Protect(GmailClientSecret);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Serviço pronto (token salvo + renovação). Null se o provedor não estiver configurado.</summary>
    [SupportedOSPlatform("windows")]
    public OAuthTokenProvider? Tokens(MailProvider provider) =>
        Config(provider) is { } c ? new OAuthTokenProvider(new OAuthPkceFlow(c), new DpapiTokenStore(provider)) : null;
}

/// <summary>Token de e-mail por provedor, cifrado com DPAPI (mail-token-outlook.json / mail-token-gmail.json).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore(MailProvider provider) : IOAuthTokenStore
{
    private sealed record Stored(string Access, string Refresh, DateTimeOffset ExpiresAt);

    private string FilePath => ExtensionSettings.File($"mail-token-{provider.ToString().ToLowerInvariant()}.json");

    public Task<OAuthToken?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) is { } s)
            {
                var refresh = Secret.Unprotect(s.Refresh);
                return Task.FromResult<OAuthToken?>(new OAuthToken(Secret.Unprotect(s.Access), refresh.Length > 0 ? refresh : null, s.ExpiresAt));
            }
        }
        catch { /* outro usuário/máquina: login de novo */ }
        return Task.FromResult<OAuthToken?>(null);
    }

    public Task SaveAsync(OAuthToken token, CancellationToken ct = default)
    {
        Directory.CreateDirectory(MailSettings.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(Secret.Protect(token.AccessToken), Secret.Protect(token.RefreshToken ?? ""), token.ExpiresAt)));
        return Task.CompletedTask;
    }

    /// <summary>Sair: apaga o token desta máquina.</summary>
    public void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
