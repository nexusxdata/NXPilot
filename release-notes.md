## NXPilot Community {versao}

> Inteligência Artificial Automática, e não autônoma — porque você está no controle.

### Como instalar e atualizar

O NXPilot agora é publicado em duas partes separadas, como o NXProject:

- **NXPilot-Setup.zip** (release fixa [Setup (instalador)](https://github.com/nexusxdata/NXPilot/releases/tag/nxpilot-setup-latest)): o instalador e a **base** (runtime .NET e bibliotecas de terceiros), **sem** os arquivos do app. Só é republicado quando a base ou o instalador mudam.
- **NXPilot-Release.zip** (esta página): o **app** (`NxPilot.exe` e as DLLs do NXPilot), com menos de 1 MB.

- **Primeira instalação:** baixe o NXPilot-Setup.zip, extraia e rode `NXPilot-Setup.exe`. Ele baixa o NXPilot-Release.zip mais novo do GitHub (com SHA-256 conferido), instala e cria o atalho. Precisa de internet.
- **Já tem o NXPilot:** abra **⚙ Configurar** e use **🔄 Atualizar para a última versão**. O app baixa só o NXPilot-Release.zip, troca os arquivos e reabre. Nenhum script é executado.
- **Voltar versão:** em **⚙ Configurar**, escolha uma versão anterior e clique em **Instalar esta versão**.

### Novidades

**🔄 Atualizar: mesmos nomes no NXPilot e no NXProject**

- **🔄 Atualizar para a última versão** (era "Verificar atualização"), **Voltar para uma versão anterior** e **Atualizar a base (Setup)** (era "Baixar Setup").
- No **NXPilot-Setup**, com o app já instalado, o botão agora é **🔄 Atualizar para a última versão** (era "Reinstalar"); sem o app, continua **Instalar**.
- A barra de download mostra **MB e porcentagem** (`45,2 MB de 93,4 MB (48%)`) e, ao terminar, **100% · download concluído** antes de instalar ou de abrir o Setup.

> Esta versão republica o **NXPilot-Setup** (textos do instalador). Quem já tem o NXPilot não precisa rodá-lo: use **🔄 Atualizar para a última versão**.
