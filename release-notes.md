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

**⚡ Hooks: tarefas disparadas por eventos**

- Na **+ Nova tarefa**, em Opções, o novo **Modo da tarefa**: **Online** (executa depois do plano) ou **⚡ Hook**.
- No Hook, o agente planeja, você aprova e faz **uma execução de teste** com um texto de exemplo no lugar de `{texto}`. Os riscos que você aprovar nesse teste ficam **pré-aprovados**. No fim, **⚡ Armar hook** deixa a tarefa em modo escuta.
- Cada evento cria uma **tarefa comum** com o plano do hook, sem pedir aprovação. Ela só pede você se disparar a menos de **20 s** do anterior, acima do **limite por hora** (ajustável por hook) ou com um risco **ALTO** que não estava no teste.
- O texto do evento entra como **dado**, nunca como instrução para o agente.
- Quem aciona: o **monitor de apps** (as linhas lidas viram o `{texto}`) e o disparo **manual** na nova tela **⚡ Hooks**, onde também dá para pausar, armar, ajustar limites e remover.
- Com outra tarefa rodando, o disparo espera na fila. No **Histórico**, o novo filtro **Todas / ⚡ Hooks / Tarefas**.

**💬 Extensão Teams**

- Os avisos do NXPilot vão para um **canal do Teams** pelo webhook do canal: Workflows (Adaptive Card) ou o Incoming Webhook antigo. A URL fica cifrada.
- Você escolhe os tipos (decisões, plano pronto e tarefa concluída, falhas, mensagens dos Canais) e de **quais assuntos**. O cartão leva o assunto com o agrupador.

**📝 Nova tarefa**

- **Opções recolhíveis:** uma linha de resumo no topo (**Local Assunto**, **Agente**, **Revisor**) e o texto com a largura toda. A janela agora pode ser redimensionada.
- **Contador de caracteres:** título 150, intenção 10.000, critérios 5.000.
- **Padrão do assunto:** grava agente, modelos e revisor de um assunto, que voltam sozinhos ao escolhê-lo.
- **Citar arquivos e pastas:** arraste para a intenção ou use 📎 / 📁. No texto fica só `[xlsx01]`, `[docx02]`, `[folder01]`, e a lista de citações vai para o agente com os caminhos (relativos ao assunto quando estão dentro dele). Nada é copiado.

**🧹 Ajustes**

- O `run-nxpilot.ps1` avisa quando o código é mais novo que o build que ele vai abrir.
