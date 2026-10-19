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

**🧠 IA Local instalada pelo próprio NXPilot-Setup**

- O passo **5 · IA Local LLaMA** ganhou o botão **Instalar IA Local (~2,5 GB)**. Ele baixa o backend do LLaMA (LLamaSharp 0.27.0, CPU) e o modelo **Qwen3-4B-2507** (Apache-2.0), com barra de progresso e Cancelar. Já instalada, o botão aparece como "Já instalada".
- É a **mesma IA Local do NXProject**: mesma pasta (a configurada nele, ou `C:\IA_LOCAL`) e mesmos arquivos. O que um instala o outro enxerga, e quem tem só o NXPilot não depende mais do NXProject para isso.
- Antes de baixar, o Setup confere o espaço em disco. O modelo só fica instalado depois de conferido: tamanho, **SHA-256 publicado pelo Hugging Face** e assinatura GGUF.

**🗂 Setup em duas abas**

- **1 · Instalação e atualização:** o .NET 10 e o NXPilot, um abaixo do outro.
- **2 · Agentes e IA:** Claude Code e Codex, o classificador ONNX e a IA Local, com **Fechar** no fim.

**📦 NXData.AgentInstaller 1.1.0**

- A biblioteca de instalação (a mesma do NXProject) agora instala também a **IA Local** e o **modelo ONNX**, além do Claude Code e do Codex.

> Esta versão republica o **NXPilot-Setup**. Quem já tem o NXPilot instalado não precisa reinstalar: o app continua atualizando por **⚙ Configurar → Verificar atualização**.
