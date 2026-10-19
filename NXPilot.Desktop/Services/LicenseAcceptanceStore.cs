// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Aceite da licença nesta máquina (por usuário do Windows), como no NXProject: clickwrap na
/// PRIMEIRA execução — o app só segue depois do "Aceitar e continuar"; instalar não aceita nada.
/// Guarda quando foi aceito e qual versão do app registrou o aceite.
/// </summary>
public static class LicenseAcceptanceStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "license.accepted");

    private sealed record Record(string? AcceptedAt, string? AppVersion);

    public static bool HasAccepted() => File.Exists(FilePath);

    /// <summary>Data do aceite; null quando o arquivo não existe ou não pode ser lido.</summary>
    public static DateTime? AcceptedOn()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(FilePath));
            return rec?.AcceptedAt is { Length: > 0 } s && DateTime.TryParse(s, out var dt) ? dt : File.GetLastWriteTime(FilePath);
        }
        catch { return null; }
    }

    public static void Persist(string? appVersion)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Record(DateTime.Now.ToString("o"), appVersion)));
        }
        catch
        {
            // Sem o registro o app volta a pedir o aceite na próxima abertura —
            // preferível a impedir o uso por falha de escrita no perfil.
        }
    }
}
