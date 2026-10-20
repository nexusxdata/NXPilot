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
