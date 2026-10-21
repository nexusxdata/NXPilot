// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NXPilot.WindowsUi;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Processos de clicar/digitar em %LOCALAPPDATA%\NXPilot\windows-processes.json. A aprovação é
/// o hash do conteúdo gravado junto: editar o arquivo à mão invalida a aprovação.
/// </summary>
public static class UiProcessStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static string FilePath => NXPilot.Integrations.ExtensionSettings.File("windows-processes.json");

    public static List<UiProcess> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<UiProcess>>(File.ReadAllText(FilePath), Json) is { } list) return list;
        }
        catch { /* arquivo inválido: começa vazio */ }
        return new List<UiProcess>();
    }

    public static void Save(IEnumerable<UiProcess> processes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(processes.ToList(), Json));
    }
}
