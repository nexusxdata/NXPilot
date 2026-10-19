// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Logo da tela Sobre: o "NX" da Nexus XData com o N azul e a perna de baixo do X em laranja
/// (a cor do NXPilot). Mesmo esquema protegido do NXProject: embutido cifrado e conferido por
/// SHA-256 depois de decifrar — não fica um PNG solto no pacote.
/// </summary>
internal static class ProtectedLogoProvider
{
    private const string ResourceName = "NXPilot.Desktop.Assets.logo-nxpilot.enc";
    private const string ExpectedPlainHash = "bbce5efd525ceb9669a755f63701c86dd1dd72bc7cec9572413326bb226ba7de";
    private static readonly byte[] Key = System.Text.Encoding.UTF8.GetBytes("NXProjectCommunityLogoKey-2026!");

    private static BitmapImage? _cachedLogo;

    public static BitmapImage GetLogoImage()
    {
        if (_cachedLogo != null) return _cachedLogo;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("O recurso protegido do logo não foi encontrado.");
        using var encrypted = new MemoryStream();
        stream.CopyTo(encrypted);
        var bytes = encrypted.ToArray().Select((value, index) => (byte)(value ^ Key[index % Key.Length])).ToArray();

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != ExpectedPlainHash)
            throw new InvalidOperationException("O logo protegido falhou na validação de integridade.");

        using var imageStream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = imageStream;
        image.EndInit();
        image.Freeze();
        return _cachedLogo = image;
    }
}
