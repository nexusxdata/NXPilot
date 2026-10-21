// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;
using NXPilot.WindowsUi;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Extensão "Automatizar janelas do Windows": apps liberados (nome do processo) em
/// %LOCALAPPDATA%\NXPilot\windows-ui.json. Começa VAZIA — nada é visto até a pessoa liberar.
/// </summary>
public sealed class WindowsUiSettings
{
    public List<string> AllowedProcesses { get; set; } = new();

    public WindowsUiPolicy Policy => new(AllowedProcesses);

    private static string FilePath => NXPilot.Integrations.ExtensionSettings.File("windows-ui.json");

    public static WindowsUiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<WindowsUiSettings>(File.ReadAllText(FilePath)) is { } s) return s;
        }
        catch { /* arquivo inválido: nada liberado */ }
        return new WindowsUiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
