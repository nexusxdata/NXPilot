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

**⧉ Work items: Azure DevOps e GitHub Projects**

- Nova extensão **Work items (DevOps · GitHub)**, em ⚙ Extensões. Ela tem configuração própria do Azure DevOps (organização, projeto e PAT) e do GitHub Projects (dono, número do projeto e token), com botão de teste para cada um.
- **Campos em branco usam a conexão que o NXProject já tem** neste usuário, somente leitura. A tela mostra qual conexão está valendo.
- A tela de **Work items** escolhe a **origem** (Azure DevOps ou GitHub Projects), o **projeto** e o **work item Project**, que é o projeto executado. Com ele escolhido, aparecem só os seus itens debaixo dele na hierarquia (Epic → Feature → Story → Task, ou as sub-issues no GitHub). As escolhas ficam lembradas.
- No GitHub entram as issues abertas e os rascunhos atribuídos a você, com o status do board. Se o corpo da issue tiver uma seção "Critérios de aceite", ela vira os critérios da Nova tarefa.

**🕘 Histórico**

- **Busca dinâmica** a partir de 3 letras, em tudo ou só no título, na intenção ou nos critérios, sem diferenciar maiúsculas nem acentos.
- **Período** opcional: hoje, últimos 7 ou 30 dias, este mês, ou datas escolhidas.
- **Clonar e executar de novo:** abre a Nova tarefa já preenchida com os dados da tarefa escolhida.
- Os botões ficam numa linha própria, abaixo do caminho do worktree.

**↻ Tentar novamente**

- Se a preparação ou o plano falham, o erro aparece no card da tarefa e o botão **Tentar novamente** pede o plano de novo, na mesma tarefa.
- Abrir um arquivo do resultado que o Windows não sabe abrir mostra o **Abrir com…**. Arquivo bloqueado mostra a mensagem e oferece tentar de novo.

**🧹 Ajustes**

- O botão "Analisar e corrigir achados" não aparece mais no painel de exemplo, só numa tarefa real com achados.
