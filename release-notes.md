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

**🔄 Atualizar**

- No **NXPilot-Setup**, com o app já instalado, o botão agora é **🔄 Atualizar para a última versão** (era "Reinstalar") — você já verificou no app. Sem o app instalado, continua **Instalar**. No app, continua **Verificar atualização**.
- **Barras com porcentagem** no app (`45,2 MB de 93,4 MB (48%)`) e no Setup (o número sobre a barra). Ao terminar, mostram **100%**, para ficar claro que acabou.

> Esta versão republica o **NXPilot-Setup**. Quem já tem o NXPilot não precisa rodá-lo: use **Verificar atualização**.
