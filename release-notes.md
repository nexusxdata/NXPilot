## NXPilot Community {versao}

> Inteligência Artificial Automática, e não autônoma — porque você está no controle.

### Como instalar e atualizar

O NXPilot agora é publicado em duas partes separadas, como o NXProject:

- **NXPilot-Setup.zip** (release fixa [Setup (instalador)](https://github.com/nexusxdata/NXPilot/releases/tag/nxpilot-setup-latest)): o instalador e a **base** (runtime .NET e bibliotecas de terceiros), **sem** os arquivos do app. Só é republicado quando a base ou o instalador mudam.
- **NXPilot-Release.zip** (esta página): o **app** (`NxPilot.exe` e as DLLs do NXPilot), com menos de 1 MB.

- **Primeira instalação:** baixe o NXPilot-Setup.zip, extraia e rode `NXPilot-Setup.exe`. Ele baixa o NXPilot-Release.zip mais novo do GitHub (com SHA-256 conferido), instala e cria o atalho. Precisa de internet.
- **Já tem o NXPilot:** abra **⚙ Configurar** e use **Verificar atualização**. O app baixa só o NXPilot-Release.zip, troca os arquivos e reabre. Nenhum script é executado.
- **Voltar versão:** em **⚙ Configurar**, escolha uma versão anterior e clique em **Instalar esta versão**.

> **Se o NXPilot fecha ao criar uma tarefa** depois de atualizar para a v1.0.0.07, falta a biblioteca do git na base: baixe e rode o [NXPilot-Setup](https://github.com/nexusxdata/NXPilot/releases/tag/nxpilot-setup-latest) uma vez. A partir desta versão, a atualização confere a base sozinha.

### Novidades

**🔄 Atualização confere o Setup primeiro (como no NXProject)**

- **Verificar atualização** agora olha a base antes de tudo. Se ela mudou, oferece o NXPilot-Setup, que instala a base nova e o app mais novo juntos, em vez de atualizar só o app e deixar faltar biblioteca.
- Instalações feitas por um Setup antigo, sem o carimbo da base, também recebem o aviso.
- Se mesmo assim faltar uma biblioteca, o NXPilot **não fecha mais**: avisa qual arquivo falta e oferece abrir a página do Setup.

**🕘 Linha do tempo da tarefa**

- Novo botão **Linha do tempo**, no Histórico e no card da tarefa.
- Os eventos ficam agrupados em árvore por etapa: Planejamento, cada fase, Verificação e Correção. Cada etapa vem recolhida, com horário e totais (pedidos, decisões suas, aprovações automáticas, erros), e cada evento expande para o texto completo.
- Filtros por Decisões, Fases e Erros, e os botões Expandir tudo e Recolher tudo.
- Aba **Fases (commits e diff):** cada commit de fase ou correção, com o diff colorido.

**🔢 Tokens no lugar do custo**

- O card da tarefa e o Histórico mostram **tokens** (gerados, lidos e do cache) em vez de dólares. O valor em dólar era uma estimativa pelo preço da API, que não é o que se paga num plano de assinatura, e podia ser somado em dobro ao retomar a sessão.

**🧹 Ajustes**

- Histórico: o texto do caminho não cobre mais o botão **Remover registro**.
