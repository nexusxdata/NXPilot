// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NXPilot.Integrations;

/// <summary>
/// Projeto PBIP novo a partir do model.json de um fluxo de dados (Dataflow Gen1) do Power BI: cada
/// entidade vira uma tabela de importação que lê a entidade publicada pelo próprio fluxo
/// (PowerPlatform.Dataflows) — sem repetir a lógica de origem. Workspace pelo ID (ou pelo nome),
/// fluxo pelo nome, com erro claro se não achar. lineageTag vem do nome (mesmo arquivo a cada geração).
/// Era o script que o agente reescrevia a cada tarefa de Power BI (centenas de linhas, muitos tokens).
/// </summary>
public static partial class PowerBiDataflowModel
{
    public sealed record Result(string PbipPath, int Tables, int Columns, List<string> Notes);

    /// <summary>
    /// Gera o projeto em <paramref name="folder"/>. Não sobrescreve projeto existente. <paramref name="localDataFolder"/>:
    /// pasta com um CSV por entidade — vira o parâmetro "pasta de dados local": preenchido, cada tabela lê o CSV
    /// (teste e demonstração sem acesso ao workspace); vazio, lê o fluxo de dados do serviço.
    /// </summary>
    public static Result Generate(string dataflowJson, string folder, string project, string workspaceName, string workspaceId, string? baseThemeFile,
        string? localDataFolder = null)
    {
        if (!SafeName().IsMatch(project)) throw new ArgumentException(NxText.T(
            $"Nome de projeto inválido: '{project}' (use letras, números, espaço, _ ou -).", $"Invalid project name: '{project}' (use letters, digits, space, _ or -).", $"Nombre de proyecto no válido: '{project}' (use letras, números, espacio, _ o -)."));
        var pbip = Path.Combine(folder, project + ".pbip");
        var modelDir = Path.Combine(folder, project + ".SemanticModel");
        var reportDir = Path.Combine(folder, project + ".Report");
        if (File.Exists(pbip) || Directory.Exists(modelDir) || Directory.Exists(reportDir))
            throw new InvalidOperationException(NxText.T($"O projeto {project} já existe — edite os arquivos dele (ou escolha outro nome).",
                $"Project {project} already exists — edit its files (or choose another name).", $"El proyecto {project} ya existe; edite sus archivos (o elija otro nombre)."));

        var df = JsonNode.Parse(File.ReadAllText(dataflowJson)) as JsonObject ?? throw new InvalidOperationException("model.json não é um objeto JSON.");
        var dataflowName = df["name"]?.GetValue<string>() ?? throw new InvalidOperationException(NxText.T(
            "O JSON não tem 'name' — não parece o model.json de um fluxo de dados (exporte pelo serviço: Fluxo de dados → Exportar .json).",
            "The JSON has no 'name' — it does not look like a dataflow model.json (export it in the service: Dataflow → Export .json).",
            "El JSON no tiene 'name'; no parece el model.json de un flujo de datos (expórtelo en el servicio: Flujo de datos → Exportar .json)."));
        var culture = df["culture"]?.GetValue<string>() is { Length: > 0 } c ? c : "pt-BR";
        var notes = new List<string>();
        var entities = new List<(string Name, string? Description, List<(string Name, string Type)> Columns, string Csv)>();
        foreach (var e in df["entities"] as JsonArray ?? [])
        {
            if (e?["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
            var cols = (e["attributes"] as JsonArray ?? []).Select(a => (Name: a?["name"]?.GetValue<string>() ?? "", Type: a?["dataType"]?.GetValue<string>() ?? "string"))
                .Where(a => a.Name.Length > 0).ToList();
            if (cols.Count == 0) { notes.Add(NxText.T($"Entidade {name} sem colunas: ficou de fora.", $"Entity {name} has no columns: left out.", $"Entidad {name} sin columnas: quedó fuera.")); continue; }
            // CSV local da entidade: o arquivo da partição do model.json (location), senão <Entidade>.csv.
            var location = (e["partitions"] as JsonArray)?.FirstOrDefault()?["location"]?.GetValue<string>();
            var csv = location is { Length: > 0 } && location.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(location.Replace('/', '\\')) : name + ".csv";
            entities.Add((name, e["description"]?.GetValue<string>(), cols, csv));
        }
        if (entities.Count == 0) throw new InvalidOperationException(NxText.T("O fluxo de dados não tem entidades com colunas.", "The dataflow has no entities with columns.", "El flujo de datos no tiene entidades con columnas."));

        var p = Params();
        var def = Path.Combine(modelDir, "definition");
        Write(pbip, $$"""
            {
              "$schema": "https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json",
              "version": "1.0",
              "artifacts": [
                {
                  "report": {
                    "path": "{{project}}.Report"
                  }
                }
              ],
              "settings": {
                "enableAutoRecovery": true
              }
            }

            """);
        Write(Path.Combine(modelDir, "definition.pbism"), "{\n  \"version\": \"4.0\",\n  \"settings\": {}\n}\n");
        Write(Path.Combine(reportDir, "definition.pbir"), $"{{\n  \"version\": \"4.0\",\n  \"datasetReference\": {{\n    \"byPath\": {{\n      \"path\": \"../{project}.SemanticModel\"\n    }}\n  }}\n}}\n");
        Write(Path.Combine(def, "database.tmdl"), "database\n\tcompatibilityLevel: 1567\n\n");

        var local = localDataFolder is { Length: > 0 } ? Path.GetFullPath(localDataFolder) : null;
        if (local != null)
            foreach (var absent in entities.Where(e => !File.Exists(Path.Combine(local, e.Csv))))
                notes.Add(NxText.T($"{absent.Name}: não achei {absent.Csv} na pasta local — essa tabela dá erro até o arquivo existir (ou deixe o parâmetro vazio para usar o fluxo).",
                    $"{absent.Name}: {absent.Csv} not found in the local folder — this table fails until the file exists (or leave the parameter empty to use the dataflow).",
                    $"{absent.Name}: no encontré {absent.Csv} en la carpeta local; esa tabla da error hasta que el archivo exista (o deje el parámetro vacío para usar el flujo)."));
        var order = string.Join(",", new[] { p.WsName, p.WsId, p.Flow }.Concat(local != null ? [p.Local] : []).Append(p.Source)
            .Concat(entities.Select(e => e.Name)).Select(n => JsonSerializer.Serialize(n)));
        Write(Path.Combine(def, "model.tmdl"),
            $"model Model\n\tculture: {culture}\n\tdefaultPowerBIDataSourceVersion: powerBI_V3\n\tsourceQueryCulture: {culture}\n\tdataAccessOptions\n\t\tlegacyRedirects\n\t\treturnErrorValuesAsNull\n\n" +
            "annotation __PBI_TimeIntelligenceEnabled = 0\n\n" +
            $"annotation PBI_QueryOrder = [{order}]\n\n" +
            "annotation PBI_ProTooling = [\"DevMode\"]\n\n" +
            string.Join("\n", entities.Select(e => "ref table " + Name(e.Name))) + "\n\n");
        Write(Path.Combine(def, "expressions.tmdl"), Expressions(p, workspaceName, workspaceId, dataflowName, local));

        var columns = 0;
        foreach (var e in entities)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(e.Description)) sb.Append("/// ").Append(OneLine(e.Description)).Append('\n');
            sb.Append("table ").Append(Name(e.Name)).Append("\n\tlineageTag: ").Append(Tag("table:" + e.Name)).Append("\n\n");
            foreach (var (col, type) in e.Columns)
            {
                var (tmdl, format, summarize, underlying) = Map(type);
                if (tmdl == null) { notes.Add(NxText.T($"{e.Name}.{col}: tipo '{type}' desconhecido — ficou texto.", $"{e.Name}.{col}: unknown type '{type}' — kept as text.", $"{e.Name}.{col}: tipo '{type}' desconocido; quedó como texto.")); (tmdl, summarize) = ("string", "none"); }
                sb.Append("\tcolumn ").Append(Name(col)).Append("\n\t\tdataType: ").Append(tmdl).Append('\n');
                if (format != null) sb.Append("\t\tformatString: ").Append(format).Append('\n');
                sb.Append("\t\tlineageTag: ").Append(Tag($"column:{e.Name}|{col}")).Append('\n');
                sb.Append("\t\tsummarizeBy: ").Append(summarize).Append("\n\t\tsourceColumn: ").Append(col).Append("\n\n");
                if (underlying != null) sb.Append("\t\tannotation UnderlyingDateTimeDataType = ").Append(underlying).Append("\n\n");
                if (tmdl is "double" or "decimal") sb.Append("\t\tannotation PBI_FormatHint = {\"isGeneralNumber\":true}\n\n");
                sb.Append("\t\tannotation SummarizationSetBy = Automatic\n\n");
                columns++;
            }
            // Uma origem só por tabela — juntar CSV e fluxo na mesma consulta faz o Power BI pedir os níveis de privacidade.
            // Com pasta local: o CSV da entidade, com os tipos do fluxo (cultura en-US: datas ISO e ponto decimal).
            // Sem: a entidade publicada no fluxo de dados.
            sb.Append("\tpartition ").Append(Name(e.Name)).Append(" = m\n\t\tmode: import\n\t\tsource =\n").Append("\t\t\t\tlet\n");
            if (local != null)
            {
                var types = string.Join(", ", e.Columns.Select(col => "{" + MText(col.Name) + ", " + MType(col.Type) + "}"));
                sb.Append("\t\t\t\t    Arquivo = Csv.Document(File.Contents(").Append(MName(p.Local)).Append(" & ").Append(MText("\\" + e.Csv))
                  .Append("), [Delimiter=\",\", Encoding=65001, QuoteStyle=QuoteStyle.Csv]),\n")
                  .Append("\t\t\t\t    Cabecalho = Table.PromoteHeaders(Arquivo, [PromoteAllScalars=true]),\n")
                  .Append("\t\t\t\t    Entidade = Table.TransformColumnTypes(Cabecalho, {").Append(types).Append("}, \"en-US\")\n");
            }
            else
                sb.Append("\t\t\t\t    Fonte = ").Append(MName(p.Source)).Append(",\n")
                  .Append("\t\t\t\t    Entidade = Fonte{[entity=").Append(MText(e.Name)).Append(",version=\"\"]}[Data]\n");
            sb.Append("\t\t\t\tin\n\t\t\t\t    Entidade\n\n")
              .Append("\tannotation PBI_ResultType = Table\n\n");
            Write(Path.Combine(def, "tables", FileName(e.Name) + ".tmdl"), sb.ToString());
        }

        // Relatório: uma página vazia (os visuais vêm depois), já com o tema base do Power BI instalado.
        var page = NxText.T("Página 1", "Page 1", "Página 1");
        var report = new JsonObject
        {
            ["config"] = """{"version":"5.43","objects":{"section":[{"properties":{"verticalAlignment":{"expr":{"Literal":{"Value":"'Top'"}}}}}]},"activeSectionIndex":0}""",
            ["sections"] = new JsonArray(new JsonObject
            {
                ["config"] = "{}", ["displayName"] = page, ["displayOption"] = 1, ["filters"] = "[]",
                ["height"] = 720.0, ["name"] = "ReportSection", ["ordinal"] = 0, ["visualContainers"] = new JsonArray(), ["width"] = 1280.0,
            }),
        };
        Write(Path.Combine(reportDir, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        if (baseThemeFile != null) PowerBiReport.Fix(reportDir, baseThemeFile);
        else notes.Add(NxText.T("Sem o Power BI Desktop instalado o relatório ficou sem tema base: rode powerbi_fix_report depois de instalar.",
            "Without Power BI Desktop installed the report has no base theme: run powerbi_fix_report after installing it.",
            "Sin Power BI Desktop instalado el informe quedó sin tema base: ejecute powerbi_fix_report después de instalarlo."));
        if (workspaceName.Trim().Length == 0 && workspaceId.Trim().Length == 0)
            notes.Add(NxText.T("Workspace não informado: preencha os parâmetros do workspace (ou configure em Extensões → Power BI) antes de atualizar.",
                "No workspace given: fill in the workspace parameters (or set it in Extensions → Power BI) before refreshing.",
                "Workspace no informado: complete los parámetros del workspace (o configúrelo en Extensiones → Power BI) antes de actualizar."));

        // Arquivos locais do Power BI Desktop não vão para o repositório.
        var ignore = Path.Combine(folder, ".gitignore");
        var existing = File.Exists(ignore) ? File.ReadAllText(ignore) : "";
        var missing = new[] { "**/.pbi/localSettings.json", "**/.pbi/cache.abf" }.Where(l => !existing.Contains(l)).ToList();
        if (missing.Count > 0)
            File.AppendAllText(ignore, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "") + string.Join("\n", missing) + "\n");

        return new Result(pbip, entities.Count, columns, notes);
    }

    /// <summary>Nomes dos parâmetros no idioma do app (o usuário vê em Transformar dados).</summary>
    private sealed record ParamNames(string WsName, string WsId, string Flow, string Source, string Local);

    private static ParamNames Params() => new(
        NxText.T("NomeWorkspace", "WorkspaceName", "NombreWorkspace"),
        NxText.T("IdWorkspace", "WorkspaceId", "IdWorkspace"),
        NxText.T("NomeFluxoDados", "DataflowName", "NombreFlujoDatos"),
        NxText.T("FluxoDados", "Dataflow", "FlujoDatos"),
        NxText.T("PastaDadosLocal", "LocalDataFolder", "CarpetaDatosLocal"));

    /// <summary>Tipo do model.json → tipo do M para ler o CSV local.</summary>
    internal static string MType(string type) => type.ToLowerInvariant() switch
    {
        "int64" or "integer" => "Int64.Type",
        "double" or "decimal" => "type number",
        "boolean" => "type logical",
        "date" => "type date",
        "datetime" or "datetimeoffset" => "type datetime",
        "time" => "type time",
        _ => "type text",
    };

    private static string Expressions(ParamNames p, string workspaceName, string workspaceId, string dataflowName, string? localFolder)
    {
        var notFoundWs = NxText.T("Workspace não encontrado", "Workspace not found", "Workspace no encontrado");
        var notFoundWsText = NxText.T(
            $"Nenhum workspace com o ID '\" & {MName(p.WsId)} & \"' ou o nome '\" & {MName(p.WsName)} & \"'. Entre com a conta (Sign in) e confira os parâmetros {p.WsId}/{p.WsName}.",
            $"No workspace with ID '\" & {MName(p.WsId)} & \"' or name '\" & {MName(p.WsName)} & \"'. Sign in and check the parameters {p.WsId}/{p.WsName}.",
            $"Ningún workspace con el ID '\" & {MName(p.WsId)} & \"' o el nombre '\" & {MName(p.WsName)} & \"'. Inicie sesión y revise los parámetros {p.WsId}/{p.WsName}.");
        var notFoundFlow = NxText.T("Fluxo de dados não encontrado", "Dataflow not found", "Flujo de datos no encontrado");
        var notFoundFlowText = NxText.T(
            $"O workspace não tem o fluxo de dados '\" & {MName(p.Flow)} & \"' (ou sua conta não tem acesso). Confira o parâmetro {p.Flow}.",
            $"The workspace has no dataflow '\" & {MName(p.Flow)} & \"' (or your account has no access). Check the parameter {p.Flow}.",
            $"El workspace no tiene el flujo de datos '\" & {MName(p.Flow)} & \"' (o su cuenta no tiene acceso). Revise el parámetro {p.Flow}.");
        string Param(string name, string value, bool required, string doc) =>
            $"/// {doc}\nexpression {Name(name)} = {MText(value)} meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired={(required ? "true" : "false")}]\n" +
            $"\tlineageTag: {Tag("expr:" + name)}\n\n\tannotation PBI_ResultType = Text\n\n";
        return
            Param(p.WsName, workspaceName.Trim(), true, NxText.T("Nome do workspace do serviço Power BI onde o fluxo de dados está publicado (reserva, se o ID não for achado).",
                "Name of the Power BI service workspace where the dataflow is published (fallback when the ID is not found).", "Nombre del workspace del servicio Power BI donde está publicado el flujo de datos (reserva si no se encuentra el ID).")) +
            Param(p.WsId, workspaceId.Trim(), false, NxText.T("ID do workspace (não muda se o workspace for renomeado).", "Workspace ID (does not change if the workspace is renamed).", "ID del workspace (no cambia si se renombra el workspace).")) +
            Param(p.Flow, dataflowName, true, NxText.T("Nome do fluxo de dados de origem.", "Name of the source dataflow.", "Nombre del flujo de datos de origen.")) +
            (localFolder == null ? "" : Param(p.Local, localFolder, true, NxText.T(
                "Pasta com um CSV por entidade: as tabelas leem daqui (teste/demonstração sem o serviço). Para ler o fluxo de dados, gere o projeto sem a pasta local.",
                "Folder with one CSV per entity: the tables read from here (test/demo without the service). To read the dataflow, generate the project without the local folder.",
                "Carpeta con un CSV por entidad: las tablas leen de aquí (prueba/demostración sin el servicio). Para leer el flujo de datos, genere el proyecto sin la carpeta local."))) +
            $"/// {NxText.T("Entidades do fluxo de dados: workspace pelo ID (ou pelo nome), fluxo pelo nome — com erro claro se não achar.", "Dataflow entities: workspace by ID (or name), dataflow by name — with a clear error when not found.", "Entidades del flujo de datos: workspace por ID (o nombre), flujo por nombre, con un error claro si no se encuentra.")}\n" +
            $"expression {Name(p.Source)} =\n" +
            "\t\tlet\n" +
            "\t\t    Fonte = PowerPlatform.Dataflows(null),\n" +
            "\t\t    Workspaces = Fonte{[Id=\"Workspaces\"]}[Data],\n" +
            $"\t\t    PorId = if {MName(p.WsId)} <> \"\" then (try Table.SelectRows(Workspaces, each [workspaceId] = {MName(p.WsId)}) otherwise #table({{}}, {{}})) else #table({{}}, {{}}),\n" +
            $"\t\t    Achados = if not Table.IsEmpty(PorId) then PorId else Table.SelectRows(Workspaces, each [workspaceName] = {MName(p.WsName)}),\n" +
            "\t\t    Workspace = if Table.IsEmpty(Achados)\n" +
            $"\t\t        then error Error.Record({MText(notFoundWs)}, \"{notFoundWsText}\")\n" +
            "\t\t        else Achados{0}[Data],\n" +
            $"\t\t    Fluxos = Table.SelectRows(Workspace, each [dataflowName] = {MName(p.Flow)}),\n" +
            "\t\t    Fluxo = if Table.IsEmpty(Fluxos)\n" +
            $"\t\t        then error Error.Record({MText(notFoundFlow)}, \"{notFoundFlowText}\")\n" +
            "\t\t        else Fluxos{0}[Data]\n" +
            "\t\tin\n" +
            "\t\t    Fluxo\n" +
            $"\tlineageTag: {Tag("expr:" + p.Source)}\n\n\tannotation PBI_ResultType = Table\n\n";
    }

    /// <summary>Tipo do model.json → (dataType TMDL, formatString, summarizeBy, UnderlyingDateTimeDataType). Nulo = desconhecido.</summary>
    internal static (string? Type, string? Format, string Summarize, string? Underlying) Map(string type) => type.ToLowerInvariant() switch
    {
        "string" => ("string", null, "none", null),
        "int64" or "integer" => ("int64", "0", "sum", null),
        "double" => ("double", null, "sum", null),
        "decimal" => ("decimal", null, "sum", null),
        "boolean" => ("boolean", null, "none", null),
        "date" => ("dateTime", "Long Date", "none", "Date"),
        "datetime" or "datetimeoffset" => ("dateTime", "General Date", "none", null),
        "time" => ("dateTime", "Long Time", "none", "Time"),
        _ => (null, null, "none", null),
    };

    /// <summary>Nome TMDL: aspas simples quando não é identificador simples.</summary>
    internal static string Name(string name) => SimpleName().IsMatch(name) ? name : "'" + name.Replace("'", "''") + "'";

    /// <summary>Nome em M: #"..." quando não é identificador simples.</summary>
    private static string MName(string name) => SimpleName().IsMatch(name) ? name : "#" + MText(name);

    private static string MText(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string FileName(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>lineageTag estável: o mesmo nome gera o mesmo GUID (gerar de novo não muda o arquivo).</summary>
    internal static string Tag(string text) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToString();

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex SimpleName();
    [GeneratedRegex(@"^[\p{L}\p{N}_][\p{L}\p{N} _\-]{0,80}$")] private static partial Regex SafeName();
}
