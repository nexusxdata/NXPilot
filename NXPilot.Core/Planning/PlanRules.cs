// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXPilot — licenciado sob a NXPilot License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

namespace NXPilot.Planning;

/// <summary>
/// Regras que vão no fim de TODO pedido de plano, por agente (editáveis em Configurar). Genéricas:
/// no plano só se lê e propõe — criar, alterar ou enviar (arquivo, work item, planilha, mensagem,
/// push…) é da execução, depois da aprovação. A trava de verdade é do NXPilot (no plano, ferramenta
/// que grava é recusada); a regra só evita o agente tentar e gastar a tarefa — o Codex, em teste,
/// tentou criar as tasks já no plano.
/// </summary>
public static class PlanRules
{
    /// <summary>Padrão do agente (Codex é mais enfático: foi ele quem se adiantou nos testes).</summary>
    public static string Default(bool codex) => codex
        ? NxText.T(
            "FASE DE PLANO — SOMENTE LEITURA E PROPOSTA. Não execute a tarefa agora. Proibido nesta fase: criar, alterar, apagar ou enviar qualquer coisa — arquivos, work items (nxproject_create_items), planilhas e apresentações (xlsx_write_cells, xlsx_build, pptx_build…), mensagens (teams_send, e-mail), git commit/push, pull requests, SQL que grave. Ferramentas que gravam serão recusadas nesta fase. Use só leitura (workitems_tree, nxproject_status, xlsx_read, pptx_read, listar e ler arquivos) e devolva o plano com as fases; a execução vem depois que o usuário aprovar.",
            "PLANNING PHASE — READ AND PROPOSE ONLY. Do not carry out the task now. Forbidden in this phase: creating, changing, deleting or sending anything — files, work items (nxproject_create_items), spreadsheets and presentations (xlsx_write_cells, xlsx_build, pptx_build…), messages (teams_send, e-mail), git commit/push, pull requests, SQL that writes. Tools that write are refused in this phase. Use read-only tools (workitems_tree, nxproject_status, xlsx_read, pptx_read, listing and reading files) and return the plan with its phases; execution comes after the user approves.",
            "FASE DE PLAN — SOLO LECTURA Y PROPUESTA. No ejecute la tarea ahora. Prohibido en esta fase: crear, modificar, borrar o enviar cualquier cosa — archivos, work items (nxproject_create_items), hojas de cálculo y presentaciones (xlsx_write_cells, xlsx_build, pptx_build…), mensajes (teams_send, correo), git commit/push, pull requests, SQL que escriba. Las herramientas que escriben se rechazan en esta fase. Use solo lectura (workitems_tree, nxproject_status, xlsx_read, pptx_read, listar y leer archivos) y devuelva el plan con sus fases; la ejecución viene después de que el usuario apruebe.")
        : NxText.T(
            "FASE DE PLANO — só leitura: leia o que precisar (arquivos, work items, dados) e proponha o plano. Não crie, não altere, não apague e não envie nada nesta fase; o que gravar ou mudar algo entra no plano como fase, para depois da aprovação do usuário.",
            "PLANNING PHASE — read-only: read what you need (files, work items, data) and propose the plan. Do not create, change, delete or send anything in this phase; whatever writes or changes something goes into the plan as a phase, for after the user approves.",
            "FASE DE PLAN — solo lectura: lea lo que necesite (archivos, work items, datos) y proponga el plan. No cree, modifique, borre ni envíe nada en esta fase; lo que escriba o cambie algo entra en el plan como fase, para después de la aprobación del usuario.");

    /// <summary>Prompt do plano + as regras do agente (vazias = sem regra).</summary>
    public static string Apply(string planPrompt, string? rules) =>
        string.IsNullOrWhiteSpace(rules) ? planPrompt : planPrompt + "\n\n" + rules.Trim();
}
