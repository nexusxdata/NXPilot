// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXPilot.Integrations;

/// <summary>
/// Pasta das configurações das extensões do MCP NXPilot (Databricks, Git, e-mail, work items,
/// Teams, Power BI, janelas do Windows): %LOCALAPPDATA%\NXPilot\MCP\Settings. Fica no perfil do
/// Windows — nunca num repositório — e o agente não lê (gate ALTO). Arquivos que estavam soltos
/// em %LOCALAPPDATA%\NXPilot (versões anteriores) são movidos para cá na primeira leitura.
/// </summary>
public static class ExtensionSettings
{
    public static string AppFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot");

    public static string Folder => Path.Combine(AppFolder, "MCP", "Settings");

    /// <summary>Caminho do arquivo de configuração <paramref name="name"/>, migrando o da pasta antiga se ainda estiver lá.</summary>
    public static string File(string name)
    {
        var path = Path.Combine(Folder, name);
        try
        {
            var old = Path.Combine(AppFolder, name);
            if (!System.IO.File.Exists(path) && System.IO.File.Exists(old))
            {
                Directory.CreateDirectory(Folder);
                System.IO.File.Move(old, path);
            }
        }
        catch { /* sem permissão ou em uso: lê/grava no caminho novo mesmo assim */ }
        return path;
    }
}
