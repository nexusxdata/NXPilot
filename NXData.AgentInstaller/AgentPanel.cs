// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXData.AgentInstaller;

/// <summary>Texto no idioma da tela: (português, inglês, espanhol) → o do idioma ativo.</summary>
public delegate string Texts(string pt, string en, string es);

/// <summary>
/// O MESMO comportamento da linha de cada agente em todas as telas que instalam agentes (NXPilot-Setup, NXPilot →
/// Configurar → Agentes de código, NXProject-Setup): o que mostrar no status, quando o Instalar vira Atualizar ou
/// "✓ Atualizado", quando a 🗑 aparece e fica habilitada (e a dica), e as perguntas de confirmação. Cada tela só
/// liga os próprios botões — a regra fica aqui, num lugar só.
/// </summary>
public static class AgentPanel
{
    public static string DisplayName(AgentCli cli) => cli == AgentCli.ClaudeCode ? "Claude Code" : "Codex";

    /// <summary>Origem do agente no idioma da tela ("extensão do VS Code", "instalador oficial", "PATH", "NXProject").</summary>
    public static string SourceName(string source, Texts t) => source switch
    {
        "instalador oficial" => t("instalador oficial", "official installer", "instalador oficial"),
        "extensão do VS Code" => t("extensão do VS Code", "VS Code extension", "extensión de VS Code"),
        _ => source,
    };

    /// <summary>"✓ 2.1.306 (extensão do VS Code)".</summary>
    public static string InstalledText(AgentLocation found, Texts t) =>
        $"✓ {found.Version ?? t("instalado", "installed", "instalado")} ({SourceName(found.Source, t)})";

    /// <summary>" · 2.1.307 disponível" (acrescentado ao status quando há versão nova).</summary>
    public static string AvailableSuffix(string latest, Texts t) =>
        t($" · {latest} disponível", $" · {latest} available", $" · {latest} disponible");

    /// <summary>"0.163.0" é mais nova que "0.162.0-alpha.17.2"? (compara só a parte numérica; ilegível = não)</summary>
    public static bool IsNewer(string target, string installed) =>
        Version.TryParse(target.Split('-')[0], out var l) && Version.TryParse(installed.Split('-')[0], out var i) && l > i;

    public enum InstallButton { Install, Update, UpToDate }

    /// <summary>
    /// Botão principal: sem agente = Instalar; versão nova = Atualizar; em dia = "✓ Atualizado" desabilitado — salvo se a
    /// última tentativa deu erro (aí continua habilitado para tentar de novo). Sem dar para comparar (sem internet): Instalar habilitado.
    /// </summary>
    public static (InstallButton Kind, bool Enabled) Install(AgentLocation? found, string? target, bool lastFailed)
    {
        if (found == null) return (InstallButton.Install, true);
        if (target == null || found.Version == null) return (InstallButton.Install, true);
        if (IsNewer(target, found.Version)) return (InstallButton.Update, true);
        return (InstallButton.UpToDate, lastFailed);
    }

    public static string InstallLabel(InstallButton kind, Texts t) => kind switch
    {
        InstallButton.Update => t("Atualizar", "Update", "Actualizar"),
        InstallButton.UpToDate => t("✓ Atualizado", "✓ Up to date", "✓ Actualizado"),
        _ => t("Instalar", "Install", "Instalar"),
    };

    public sealed record RemoveState(bool Visible, bool Enabled, string Tip);

    /// <summary>
    /// 🗑: aparece sempre que há o agente; só remove o que está nas pastas do instalador oficial (instalado pelo NXPilot
    /// ou pelo Setup). Da extensão do VS Code (ou npm etc.) fica desabilitada, com a dica de onde remover — apagar ali
    /// quebraria a extensão.
    /// </summary>
    public static RemoveState Remove(AgentInstaller installer, AgentInstallOptions options, AgentCli cli, Texts t)
    {
        var located = AgentLocator.Candidates(cli, options).FirstOrDefault(); // sem rodar --version
        var ours = installer.CanUninstall(cli);
        var name = DisplayName(cli);
        var tip = ours
            ? t($"Remover o {name} deste computador (pergunta antes; seu login é mantido)", $"Remove {name} from this computer (asks first; your sign-in is kept)", $"Quitar {name} de esta computadora (pregunta antes; se mantiene su inicio de sesión)")
            : located?.Source == "extensão do VS Code"
                ? t("Este vem da extensão do VS Code: remova pela própria extensão (Extensões → Desinstalar). Apagar aqui quebraria a extensão.",
                    "This one comes from the VS Code extension: remove it from the extension itself (Extensions → Uninstall). Deleting it here would break the extension.",
                    "Este viene de la extensión de VS Code: quítelo desde la propia extensión (Extensiones → Desinstalar). Borrarlo aquí rompería la extensión.")
                : t($"Este não foi instalado por aqui ({located?.Source}): remova pelo mesmo jeito que instalou.",
                    $"This one was not installed from here ({located?.Source}): remove it the same way you installed it.",
                    $"Este no se instaló desde aquí ({located?.Source}): quítelo de la misma forma en que lo instaló.");
        return new RemoveState(located != null || ours, ours, tip);
    }

    /// <summary>Pergunta antes de remover (todas as telas usam a mesma).</summary>
    public static string RemoveQuestion(AgentCli cli, Texts t)
    {
        var name = DisplayName(cli);
        return t($"Remover o {name} deste computador?\n\nTarefas que usam o {name} deixam de funcionar até instalar de novo. Seu login e suas configurações são mantidos.",
                 $"Remove {name} from this computer?\n\nTasks that use {name} stop working until you install it again. Your sign-in and settings are kept.",
                 $"¿Quitar {name} de esta computadora?\n\nLas tareas que usan {name} dejan de funcionar hasta instalarlo de nuevo. Se mantienen su inicio de sesión y su configuración.");
    }
}
