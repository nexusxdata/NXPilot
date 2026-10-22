// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.IO;
using System.Text.Json;

namespace NXPilot.Desktop.Services;

/// <summary>
/// Lixo do Histórico: exclusão LÓGICA. A tarefa só some da lista — worktree, branch, registro e o que já
/// foi para o GitHub ficam onde estão (no GitHub nunca se apaga de verdade). Fica num arquivo à parte
/// (lixo.json, id → quando), para o registro da tarefa não mudar; a quarentena local vem depois.
/// </summary>
public static class TaskTrash
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXPilot", "lixo.json");

    public static Dictionary<string, DateTime> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch { return new(); } // arquivo estragado: nada no lixo (a tarefa reaparece, nunca some de vez)
    }

    public static bool Contains(string id) => Load().ContainsKey(id);

    public static void Move(string id) => Update(t => t[id] = DateTime.Now);

    public static void Restore(string id) => Update(t => t.Remove(id));

    private static void Update(Action<Dictionary<string, DateTime>> change)
    {
        var trash = Load();
        change(trash);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(trash, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
