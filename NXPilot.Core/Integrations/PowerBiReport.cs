// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Text.Json;
using System.Text.Json.Nodes;

namespace NXPilot.Integrations;

/// <summary>
/// Esqueleto do relatório de um projeto PBIP. Um relatório escrito à mão sem o tema base
/// (themeCollection + resourcePackages + o arquivo do tema) abre no Power BI Desktop com
/// "Something went wrong / Failed to load the report" e a página em branco — o modelo carrega,
/// o relatório não. Vale para o formato antigo (report.json na pasta .Report) e para o PBIR
/// (definition/report.json).
/// </summary>
public static class PowerBiReport
{
    private const string ThemesFolder = "BaseThemes";

    /// <summary>report.json do relatório (PBIR primeiro) e se é o formato PBIR.</summary>
    public static (string? Path, bool Pbir) ReportJson(string reportDir)
    {
        var pbir = Path.Combine(reportDir, "definition", "report.json");
        if (File.Exists(pbir)) return (pbir, true);
        var legacy = Path.Combine(reportDir, "report.json");
        return File.Exists(legacy) ? (legacy, false) : (null, false);
    }

    /// <summary>Problemas do esqueleto que impedem o Power BI de abrir o relatório.</summary>
    public static List<string> Problems(string reportDir, string worktree)
    {
        var problems = new List<string>();
        var (path, pbir) = ReportJson(reportDir);
        var relDir = System.IO.Path.GetRelativePath(worktree, reportDir);
        if (path == null)
        {
            problems.Add($"{relDir}: sem report.json (nem definition/report.json) — o Power BI não tem relatório para abrir.");
            return problems;
        }
        var rel = System.IO.Path.GetRelativePath(worktree, path);
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("não é um objeto"); }
        catch (Exception ex) { problems.Add($"{rel}: JSON inválido — {ex.Message}"); return problems; }

        if (pbir)
            foreach (var kind in new[] { "baseTheme", "customTheme" })
                if (root["themeCollection"]?[kind]?["reportVersionAtImport"] is JsonValue)
                    problems.Add($"{rel}: themeCollection.{kind}.reportVersionAtImport é texto, mas no PBIR é um objeto {{\"visual\",\"report\",\"page\"}} — o Power BI mostra \"Your report has issues that could not be resolved\". Corrija com powerbi_fix_report.");
        var theme = BaseThemeName(root, pbir);
        const string fix = " O Power BI Desktop mostra 'Something went wrong' e a página em branco. Corrija com powerbi_fix_report.";
        if (theme == null)
        {
            problems.Add($"{rel}: sem tema base (themeCollection.baseTheme).{fix}");
            return problems;
        }
        if (!HasThemePackage(root, theme, pbir))
            problems.Add($"{rel}: o tema base '{theme}' não está em resourcePackages (SharedResources).{fix}");
        if (!File.Exists(ThemeFile(reportDir, theme)))
            problems.Add($"{relDir}: falta o arquivo do tema base StaticResources/SharedResources/{ThemesFolder}/{theme}.json.{fix}");
        return problems;
    }

    /// <summary>
    /// Parâmetros do modelo ainda com valor provisório (INFORMAR_…, TODO, &lt;workspace&gt;): a atualização
    /// falha com "There weren't enough elements in the enumeration". Aponta o workspace configurado.
    /// </summary>
    public static List<string> PlaceholderProblems(string semanticModelDir, string worktree)
    {
        var problems = new List<string>();
        var file = System.IO.Path.Combine(semanticModelDir, "definition", "expressions.tmdl");
        if (!File.Exists(file)) return problems;
        var settings = PowerBiSettings.Load();
        var lines = File.ReadAllLines(file);
        var placeholder = new System.Text.RegularExpressions.Regex(@"^expression\s+('?[^'=]+'?)\s*=\s*""(INFORMAR[^""]*|TODO[^""]*|<[^""]*>|PREENCHER[^""]*|FILL[_ ]?IN[^""]*)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Length; i++)
            if (placeholder.Match(lines[i]) is { Success: true } m)
                problems.Add($"{System.IO.Path.GetRelativePath(worktree, file)}:{i + 1}: o parâmetro {m.Groups[1].Value.Trim()} está com o valor provisório \"{m.Groups[2].Value}\" — " +
                             "a atualização dos dados falha (\"There weren't enough elements in the enumeration\"). " +
                             (settings.HasWorkspace ? settings.AgentLine() : "Pergunte o valor ao usuário ou peça para configurar o workspace de novos PBI na extensão Power BI do NXPilot."));
        return problems;
    }

    /// <summary>
    /// reportVersionAtImport do PBIR: um OBJETO com as versões de visual, relatório e página (texto como
    /// "5.70" é do formato antigo — o Power BI recusa com "was not provided as the correct type").
    /// Mantém o que o relatório já tiver; senão, versões que o Power BI Desktop 2.159 abre.
    /// </summary>
    public static JsonNode VersionAtImport(JsonObject pbirReport) =>
        (pbirReport["themeCollection"]?["baseTheme"]?["reportVersionAtImport"] ?? pbirReport["themeCollection"]?["customTheme"]?["reportVersionAtImport"]) is JsonObject existing
            ? existing.DeepClone()
            : new JsonObject { ["visual"] = "2.6.0", ["report"] = "3.1.0", ["page"] = "2.3.0" };

    public static string ThemeFile(string reportDir, string theme) =>
        System.IO.Path.Combine(reportDir, "StaticResources", "SharedResources", ThemesFolder, theme + ".json");

    private static string? BaseThemeName(JsonObject root, bool pbir)
    {
        JsonNode? collection = pbir ? root["themeCollection"] : ParseConfig(root)?["themeCollection"];
        return collection?["baseTheme"]?["name"]?.GetValue<string>() is { Length: > 0 } name ? name : null;
    }

    private static JsonObject? ParseConfig(JsonObject root)
    {
        try { return root["config"] is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) as JsonObject : root["config"] as JsonObject; }
        catch { return null; }
    }

    private static bool HasThemePackage(JsonObject root, string theme, bool pbir) =>
        (root["resourcePackages"] as JsonArray ?? []).Any(p =>
        {
            var pkg = pbir ? p : p?["resourcePackage"];
            return pkg?["name"]?.GetValue<string>() == "SharedResources" &&
                   (pkg["items"] as JsonArray ?? []).Any(i => i?["name"]?.GetValue<string>() == theme);
        });

    /// <summary>
    /// Completa o esqueleto com o tema base <paramref name="themeFile"/> (copiado da instalação do
    /// Power BI Desktop): themeCollection, resourcePackages e o arquivo do tema. Não mexe em páginas
    /// nem em visuais. Devolve o que foi feito.
    /// </summary>
    public static string Fix(string reportDir, string themeFile)
    {
        var (path, pbir) = ReportJson(reportDir);
        if (path == null) throw new InvalidOperationException("Relatório sem report.json — crie as páginas primeiro.");
        var theme = System.IO.Path.GetFileNameWithoutExtension(themeFile);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException("report.json não é um objeto JSON.");
        if (pbir)
        {
            // Só o tema base muda: um tema personalizado que já esteja lá continua.
            var version = VersionAtImport(root);
            var collection = root["themeCollection"] as JsonObject ?? new JsonObject();
            collection["baseTheme"] = new JsonObject { ["name"] = theme, ["reportVersionAtImport"] = version, ["type"] = "SharedResources" };
            if (collection["customTheme"] is JsonObject custom && custom["reportVersionAtImport"] is JsonValue) custom["reportVersionAtImport"] = version.DeepClone();
            root["themeCollection"] = collection;
            var packages = root["resourcePackages"] as JsonArray ?? new JsonArray();
            if (!HasThemePackage(root, theme, pbir: true))
            {
                foreach (var old in packages.Where(p => p?["name"]?.GetValue<string>() == "SharedResources").ToList()) packages.Remove(old);
                packages.Add(new JsonObject
                {
                    ["name"] = "SharedResources", ["type"] = "SharedResources",
                    ["items"] = new JsonArray(new JsonObject { ["name"] = theme, ["path"] = $"{ThemesFolder}/{theme}.json", ["type"] = "BaseTheme" }),
                });
            }
            root["resourcePackages"] = packages;
        }
        else
        {
            var config = ParseConfig(root) ?? new JsonObject();
            config["version"] ??= "5.43";
            config["themeCollection"] = new JsonObject { ["baseTheme"] = new JsonObject { ["name"] = theme, ["version"] = "5.70", ["type"] = 2 } };
            config["activeSectionIndex"] ??= 0;
            root["config"] = config.ToJsonString();
            var packages = root["resourcePackages"] as JsonArray ?? new JsonArray();
            if (!HasThemePackage(root, theme, pbir: false))
            {
                foreach (var old in packages.Where(p => p?["resourcePackage"]?["name"]?.GetValue<string>() == "SharedResources").ToList()) packages.Remove(old);
                packages.Add(new JsonObject
                {
                    ["resourcePackage"] = new JsonObject
                    {
                        ["name"] = "SharedResources", ["type"] = 2, ["disabled"] = false,
                        ["items"] = new JsonArray(new JsonObject { ["type"] = 202, ["path"] = $"{ThemesFolder}/{theme}.json", ["name"] = theme }),
                    },
                });
            }
            root["resourcePackages"] = packages;
            var ordinal = 0;
            foreach (var section in root["sections"] as JsonArray ?? []) if (section is JsonObject s) s["ordinal"] ??= ordinal++;
            root["layoutOptimization"] ??= 0;
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var target = ThemeFile(reportDir, theme);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(themeFile, target, overwrite: true);
        return $"Tema base {theme} aplicado em {System.IO.Path.GetFileName(path)} ({(pbir ? "PBIR" : "formato antigo")}) e copiado para StaticResources/SharedResources/{ThemesFolder}.";
    }
}

/// <summary>
/// Extensão Power BI: o workspace onde ficam os dataflows/modelos que os PBI novos usam. O agente
/// recebe pelo powerbi_info e grava nos parâmetros do projeto em vez de um valor provisório. Sem segredo.
/// </summary>
public sealed class PowerBiSettings
{
    public string NewWorkspaceName { get; set; } = "";
    public string NewWorkspaceId { get; set; } = "";

    public bool HasWorkspace => NewWorkspaceName.Trim().Length > 0 || NewWorkspaceId.Trim().Length > 0;

    /// <summary>Linha para o agente (vazia sem workspace configurado).</summary>
    public string AgentLine() => !HasWorkspace ? "" :
        $"Workspace for new Power BI reports (configured by the user in NXPilot): name \"{NewWorkspaceName.Trim()}\"" +
        (NewWorkspaceId.Trim().Length > 0 ? $", id {NewWorkspaceId.Trim()}" : "") +
        " — use it in the project parameters (never a placeholder like INFORMAR_WORKSPACE).";

    private static string FilePath => NXPilot.Integrations.ExtensionSettings.File("powerbi.json");

    public static PowerBiSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<PowerBiSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
