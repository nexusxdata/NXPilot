## NXPilot Community {versao}

> Inteligência Artificial Automática, e não autônoma — porque você está no controle.

### Como instalar e atualizar

O NXPilot agora é publicado em duas partes separadas, como o NXProject:

- **NXPilot-Setup.zip** (release fixa [Setup (instalador)](https://github.com/nexusxdata/NXPilot/releases/tag/nxpilot-setup-latest)): o instalador e a **base** (runtime .NET e bibliotecas de terceiros), **sem** os arquivos do app. Só é republicado quando a base ou o instalador mudam.
- **NXPilot-Release.zip** (esta página): o **app** (`NxPilot.exe` e as DLLs do NXPilot), com menos de 1 MB.

- **Primeira instalação:** baixe o NXPilot-Setup.zip, extraia e rode `NXPilot-Setup.exe`. Ele baixa o NXPilot-Release.zip mais novo do GitHub (com SHA-256 conferido), instala e cria o atalho. Precisa de internet.
- **Já tem o NXPilot:** abra **⚙ Configurar** e use **Verificar atualização**. O app baixa só o NXPilot-Release.zip, troca os arquivos e reabre. Nenhum script é executado.
- **Voltar versão:** em **⚙ Configurar**, escolha uma versão anterior e clique em **Instalar esta versão**.

### Novidades

**🛠 Ferramentas → Amostra de dados (Databricks)**

- Item novo no menu, antes de Configurar. Consulta **só de leitura**, com no máximo **50 registros** (o `LIMIT 50` entra sempre) — para conferir dados, não para extrair.
- **Vertical** (cada coluna numa linha, cada registro numa coluna, como o modelo do Power BI) ou horizontal; números à direita com separador e as casas do tipo, datas dd/mm/aaaa, Sim/Não; cada registro numa faixa de cor.
- Enquanto roda: barra, tempo correndo e **Cancelar** (cancela no warehouse também).
- **Exportar para Excel** no mesmo formato da tela, com números e datas como valores do Excel, cabeçalho com filtro e faixas por registro, e a aba **SQL** (consulta, o que rodou, data, registros e tempo).
- **Recentes**: guarda só o SQL e quanto demorou da última vez — nunca os dados. Abre com a última consulta (ou os schemas do catálogo da conexão).

**🗄 Databricks**

- **Comentários**: ao criar uma view/tabela com base em outra, o agente leva o comentário do objeto e das colunas (`databricks_comments` devolve o SQL pronto); `databricks_describe_table` mostra os comentários.
- **Schemas de gravação** passam a ser obrigatórios na configuração (use `*` para liberar todos).
- Resultados de consultas do agente aparecem na tela, mas **não ficam gravados** no Histórico (o SQL fica).

**🔍 Verificação cruzada mais justa**

- O revisor recebe o que a tarefa fez fora do repositório (ex.: a view criada no Databricks) e as consultas usadas como evidência; o revisor Codex também consulta o MCP (só leitura).
- Vê a contestação do executor nas rodadas anteriores e confere antes de repetir o achado; a correção automática para quando o executor contesta sem alterar nada.

**🔔 Avisos**

- "Plano pronto para aprovar" sai das notificações quando você aprova, descarta ou edita o plano.

**📊 Power BI: criar PBI novo com pente fino, workspace e PBI de exemplo**

- As ferramentas Power BI aparecem mesmo com o repositório vazio (com o Power BI Desktop instalado): o agente cria o projeto e depois valida e abre no Desktop, corrigindo até carregar sem erro.
- `powerbi_validate` aponta relatório sem tema base (o Power BI mostrava "Something went wrong" e página em branco), versão do tema no tipo errado e parâmetro com valor provisório (ex.: INFORMAR_WORKSPACE). `powerbi_fix_report` completa o tema base copiado do seu Power BI instalado.
- `powerbi_capture` avisa quando o Power BI abre uma janela de erro, com o texto dela.
- **Workspace de novos PBI** na aba Power BI da Extensão MCP: o agente grava nos parâmetros do projeto.
- **PBI de exemplo**: cite o `.pbip`, a pasta ou o `.pbix` na tarefa e diga a aba — `powerbi_style` lê o estilo (tamanho, fundo, tema, posição e formatação de cada visual) e `powerbi_copy_theme` copia o tema e as imagens (ex.: fundo da página). Sem exemplo, segue sem.
- Arquivo citado na tarefa é lido sem pedir aprovação; gravar fora do worktree continua ALTO.

**🔧 Verificação cruzada: correção visível e automática**

- Cada "Analisar e corrigir achados" vira uma linha na aba Fases ("Correção dos achados · rodada N"), com barra, terminal, log e os achados enviados.
- Opção na Nova tarefa: **corrigir os achados automaticamente** (até 2 rodadas, cada ação passa pelo gate).
- Histórico → **🔎 Achados e correções**: o que foi achado em cada rodada, o que o agente fez e o resultado final.
- Revisor Codex sem modelo definido usa o modelo testado (gpt-5.6-terra); revisor que falha não vira "achado".

**🗂 Aba Fases: terminal e log por fase**

- Ao aprovar o plano, a tela vai para a aba Fases. Cada fase tem **Detalhar** (a saída do agente só daquela fase) e o log da fase embaixo; o último evento (resultado ou erro) aparece abaixo de cada fase.

**🛡 Databricks: schemas de gravação**

- Novo campo na aba Databricks (ex.: `sandbox_*`, `dev.sandbox_*`): criar e alterar objetos só nesses schemas (MÉDIO); fora deles, a escrita é bloqueada — protege a produção. Destino incerto pergunta (ALTO). Vazio = como antes.

**📋 Histórico e avisos**

- Filtro por **Assunto** (um assunto, todos, ou sem assunto).
- A notificação "Decisão pendente" sai da lista quando você aprova ou nega.

**⏱ Work items**

- `workitems_tree` mostra estimativa original, horas trabalhadas, estimativa atual (HH Atual), restante e data de criação; quando a referência cresceu, o agente avisa no plano e lista as tasks que entraram depois.

**🔒 Configurações das extensões em pasta própria**

- Databricks, Git, e-mail, work items, Teams, Power BI e janelas do Windows ficam em `%LOCALAPPDATA%\NXPilot\MCP\Settings` (movidas sozinhas na primeira abertura). Só na sua máquina; o agente não lê (ALTO).

**⏱ Work items: horas completas e aviso quando a referência cresceu**

- `workitems_tree` agora mostra, por item, a estimativa original, as horas trabalhadas, a estimativa atual (HH Atual do NXProject), o restante e a data de criação.
- Quando a estimativa atual passou da original (ex.: Story de 22 h que terminou com 31 h), a ferramenta aponta as tasks que entraram depois do plano original. No plano, o agente avisa com os números e lista essas tasks para você decidir se entram; as tasks novas levam a estimativa original.

**🖱 Duplo clique só sobre a linha**

- Na lista de Work items, no Histórico e nas Tarefas modelo, dois cliques na barra de rolagem, no cabeçalho ou no vazio não abrem mais nada (antes criavam a tarefa, abriam o histórico ou disparavam a tarefa modelo).

**🔗 Extensão NXProject — criar work items pelo NXProject (DevOps e GitProject)**

- Os agentes criam itens (ex.: as Tasks de uma Story, uma Story numa Feature) pela ferramenta MCP **`nxproject_create_items`**, que passa pelo **NXProject instalado**: os campos e padrões dele e a regra **Adm_NX** valem. Se não der para conferir a regra, nada é criado; cada criação passa pela sua aprovação (MÉDIO). Só cria — nunca altera nem apaga.
- **`nxproject_status`**: dono do PAT/token e se pode gravar ali. **`workitems_tree`** agora lê também o **GitHub Projects** (`source: gitproject`).
- Aba **NXProject** na Extensão MCP: conexão do DevOps e do GitProject, PAT mascarado, botão **Conferir dono e permissão**.
- O NXPilot carrega o NXProject instalado pela memória: ele atualiza com o NXPilot aberto, e sem NXProject (ou antigo) nada quebra — a extensão só pede para atualizar. Requer o **NXProject 1.2.0.25** ou mais novo.

**🧭 Regras do plano por agente (Configurar)**

- Um texto para Claude Code, Codex e NXAgentic que vai no fim de todo pedido de plano: no plano só se lê e propõe; criar, alterar ou enviar algo fica para a execução, depois da sua aprovação. Editável, com **Restaurar padrão**.

**🔒 Segredos fora do alcance do agente**

- Viram **ALTO** no gate: ler as conexões e tokens do NXPilot, do NXProject e dos agentes, decifrar segredos do Windows (DPAPI), carregar as DLLs do NXProject/NXPilot por fora e chamar a API do DevOps/GitHub direto.

**🛠 Correções**

- **Codex** agora consegue usar as ferramentas do MCP NXPilot que gravam (a aprovação era respondida no formato errado e o Codex entendia recusa).
- Work items citados: item de referência (ex.: story encerrada) é lido inteiro; tarefa de criar work items entrega os itens, não código.

**📎 Work items citados na tarefa, como anexos — vários de uma vez**

- Na lista de Work items, marque ☐ **um ou mais itens de qualquer nível** (Project, EPIC, EPIC_GROUP, Feature, Story, Task). As marcas continuam ao trocar o Work Item Project, o projeto ou o estado — dá para juntar a Story nova e a encerrada de referência. **"Criar tarefa com N itens"**.
- Cada item vira uma citação na Nova tarefa: **[story01]**, **[story02]**, **[task01]**, **[epic01]**… A legenda diz ao agente como ler cada um (`workitems_tree`). Ex.: "crie as tasks da [story01] tendo como base as da [story02]".
- Filtro de **Estado**: Abertas, Em execução, Fechadas ou Todas (os pais ficam para dar contexto).

**🛑 Plano que não existe não é aprovado**

- Se o agente não consegue propor fases (falta dado, ferramenta ou acesso), a tarefa fica **Falhou** com o motivo — sem "Aprovar plano e executar"; ficam Tentar novamente e ✎ Editar e refazer o plano.

**🤖 Codex**

- O modelo testado (**gpt-5.6-terra**) sempre aparece e vem escolhido, mesmo se a lista da conta não carregar; modelo que a conta não aceita volta para o testado.
- Sem login no Claude Code (que precisa de assinatura), a Nova tarefa já vem com o **Codex** (funciona com a conta ChatGPT).
- O card diz o agente certo ("Codex está planejando…").

**🌳 Work items: árvore do Work Item Project e tarefa vinculada**

- Na lista de Work items (Azure DevOps), **Mostrar**: "Só as minhas · abertas" ou **"Todas as abertas · hierarquia do Work Item Project"** — uma árvore Project → EPIC/EPIC_GROUP → Feature → Story → Task, com ▶/▼ (ou setas → ←), Expandir tudo/Recolher e a quantidade de itens abaixo de cada um. Pais já fechados ficam na árvore quando têm filhos pendentes.
- Nova coluna **Responsável**.
- Qualquer nível vira tarefa, e o **vínculo** vai junto: o agente é avisado de que os dados vêm do DevOps e lê o item e tudo abaixo dele com a nova ferramenta MCP **`workitems_tree`** (tipo, estado, responsável, horas, iteração).
- A tela guarda origem, projeto, Work Item Project e o "Mostrar"; salvar na Extensão MCP não apaga mais essas escolhas.

**✎ Editar e refazer o plano**

- O botão aparece também com o plano pronto aguardando aprovação: o plano é descartado e um novo é pedido com o que você mudou (inclusive o agente).

**🧩 Extensão MCP: escolha por lista**

- A janela Extensão MCP troca a faixa de abas por uma lista **Extensão**: cabem quantas extensões vierem, cada uma com a bolinha de status, e escolher mostra a configuração dela.

**⚠ Riscos de cada tarefa no Histórico**

- Cada decisão do gate fica guardada na tarefa: nível (▲ Alto, ● Médio, ▼ Baixo), ação, agente e o que aconteceu (passou sozinho, regra da tarefa, aprovado ou negado por você). Tarefas antigas têm os riscos recuperados do Log.
- Histórico: coluna **Riscos** com a contagem colorida e filtro por nível (com Alto, com Médio, com Baixo, sem decisões).
- **⚠ Riscos da tarefa** e **⚠ Riscos da lista** abrem a lista de decisões, com filtro por nível e por resultado.

**📗 Excel no MCP NXPilot (sem Python)**

- `xlsx_info` e `xlsx_read`: resumo do arquivo e leitura em tabela com limite de linhas — datas aparecem como data e fórmulas ficam à vista. O agente lê o cabeçalho e uma amostra, em vez de gerar script para isso.
- `xlsx_write_cells`, `xlsx_append_rows`, `xlsx_add_sheet` e `xlsx_build`: valores, fórmulas e datas mantendo a formatação existente; linhas novas entram na tabela do Excel; aba nova e planilha nova com cabeçalho congelado e filtro.
- `xlsx_recalculate` (com o Excel instalado, invisível e sem macros): calcula as fórmulas, grava os valores e aponta as células com erro (#REF!, #DIV/0!…).
- Cada chamada passa pelo gate: ler segue sozinho; gravar fora do worktree é ALTO. Para transformar milhares de linhas, o script continua sendo o caminho mais barato.

**🔐 Login do Claude Code e do Codex pelo navegador**

- Sem login, o NXPilot abre o login oficial do agente (`claude auth login` / `codex login`), que leva ao navegador, como no VS Code. A tarefa continua quando você termina, e o login fica registrado no Log da tarefa.

**⬆ Agente antigo: aviso para atualizar**

- Claude Code sem a opção `--permission-prompts` (que garante que só o gate libera as ações) não roda: a mensagem mostra a versão, o executável e como atualizar.
- Claude ou Codex que falham por não conhecer uma opção ou comando do NXPilot mostram o mesmo pedido para atualizar.
- **NXPilot-Setup:** com versão nova disponível, o botão do Claude Code e do Codex vira **Atualizar** e mostra a versão. Já atualizado, o botão fica **✓ Atualizado** e desabilitado (volta a habilitar se a instalação deu erro).

**✎ Editar e tentar novamente**

- Tarefa que falhou antes do plano: além de Tentar novamente, dá para editar texto, critérios, **agente**, modelos e revisor e pedir o plano de novo. A pasta e o modo continuam os mesmos.

**📋 Histórico: coluna e filtro de Estado**

- Em andamento, Aguardando você, Concluída, Aviso, Erro, Interrompida, Descartada e Em escuta, com cor e filtro próprio. A descrição longa virou a coluna Detalhe.

**🧩 Extensões agora se chama Extensão MCP**

- O botão do menu e a janela passam a se chamar **Extensão MCP** (EN: MCP Extension; ES: Extensión MCP): é ali que se configura o que o agente usa pelo MCP NXPilot. As mensagens do app e os READMEs também usam o nome novo.

**🔌 MCP NXPilot — as extensões viram ferramentas do agente**

- Em cada tarefa, o Claude Code e o Codex recebem um servidor MCP, o **MCP NXPilot**. Power BI, PowerPoint, Databricks, Git, e-mail, work items, Teams e os apps do Windows que você liberou passam a ser ferramentas que o agente chama direto, sem escrever script para isso.
- **Cada chamada passa pelo gate**, com o mesmo painel de decisões: o que só lê passa sozinho; gravar fora do worktree é ALTO; escrita no Databricks, push, PR, ler um e-mail e mandar mensagem no Teams pedem a sua aprovação. No plano e na revisão, o MCP só lê.
- Os tokens e as conexões ficam no NXPilot: o agente nunca os vê. Sem o NXPilot aberto, nenhuma ferramenta executa.
- Só aparecem as extensões configuradas (Power BI, quando o worktree tem um projeto PBIP).
- **⚙ Configurar → MCP NXPilot:** ligar e parar, quantas ferramentas o agente vê e a última chamada. O estado fica guardado: parado continua parado ao reabrir o NXPilot.

**📊 PowerPoint sem Python**

- `pptx_build` monta a apresentação a partir de uma lista de slides em JSON: capa, seção, tópicos, duas colunas, tabela, imagem, indicadores e barras, com o tema NX ou o tema de um modelo seu.
- Também lê (em markdown), troca texto, acrescenta, apaga e reordena slides de um .pptx existente.
- `pptx_render` mostra os slides como imagem (pelo PowerPoint instalado, sem abrir janela): o agente olha o resultado e corrige o que ficou ruim.
- Usa o Open XML SDK (licença MIT), que entra na base do Setup.

**📈 Power BI que o agente consegue conferir**

- `powerbi_validate` confere o JSON e o TMDL do projeto; `powerbi_capture` abre o relatório no Power BI Desktop e devolve a imagem ao agente.

- O README (PT, EN e ES) explica o MCP NXPilot: o agente usa as extensões direto, sem gerar código para tarefa repetitiva — menos tokens, menos tentativa e erro.

> O **NXPilot-Setup** foi republicado na v1.0.0.17 (biblioteca nova na base). Se você ainda está numa versão anterior, **Verificar atualização** avisa e segue pelo Setup, que instala a base e o app juntos.
