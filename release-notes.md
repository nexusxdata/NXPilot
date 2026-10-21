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
