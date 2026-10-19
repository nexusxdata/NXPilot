# NXData.AgentInstaller

Biblioteca compartilhada pelo **NXPilot** e pelo **NXProject** para instalar e localizar os agentes de IA no Windows (**Claude Code** e **Codex CLI**) e os modelos locais (**IA Local LLaMA** e **ONNX**).
Não tem dependências.

```
dotnet add package NXData.AgentInstaller
```

## Modos

| Modo | Como funciona | Quando usar |
|---|---|---|
| **Interno** (`InstallMode.Internal`) | O próprio .NET baixa o binário, confere o **SHA-256** publicado pelo fornecedor e instala nas **mesmas pastas do instalador oficial**. Não roda nenhum PowerShell. | Padrão. Também é a saída quando o antivírus bloqueia script. |
| **Script** (`InstallMode.Script`) | Roda o instalador oficial (`claude.ai/install.ps1` ou `chatgpt.com/codex/install.ps1`) numa janela do PowerShell **visível**. | Quando você prefere o caminho oficial ou o formato de distribuição mudou. |

### O que o modo interno faz

- **Claude Code:** obtém a versão em `downloads.claude.ai/claude-code-releases/latest`, pega o checksum no `manifest.json`, baixa o `claude.exe` e confere o SHA-256. Depois roda `claude.exe install <latest|versão>`, o passo oficial que monta `~/.local/bin`.
- **Codex:** obtém a versão em `releases.openai.com/codex` (ou no GitHub, se ele estiver fora do ar). Baixa o `codex-package_SHA256SUMS` e o **pacote completo** (`codex.exe`, sandbox do Windows, `rg`, command-runner), confere o SHA-256 e extrai em `~/.codex/packages/standalone/releases/<versão>`. Por fim, cria as junções `current` e `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`, como o `install.ps1` oficial.

## Versão

- `VersionChoice.Latest`: instala a última versão publicada. O agente continua se atualizando sozinho.
- `VersionChoice.Tested`: instala a versão testada pelo produto (`AgentInstallOptions.TestedClaudeVersion` e `TestedCodexVersion`). Se essa versão **não existir mais**, a biblioteca instala a última e avisa (`FellBackToLatest`). Isso evita o erro de tentar instalar uma versão retirada.

Cada produto pode informar as versões que **ele** testou:

```csharp
var installer = new AgentInstaller(new AgentInstallOptions { TestedCodexVersion = "0.162.0" });
var r = await installer.InstallAsync(AgentCli.Codex, InstallMode.Internal, VersionChoice.Tested,
                                     new Progress<string>(Console.WriteLine));
Console.WriteLine(r.Message);   // "Codex 0.162.0 instalado." ou o aviso de queda para a última

var claude = AgentLocator.Find(AgentCli.ClaudeCode);   // PATH, instalador oficial, NXProject, extensão do VS Code
```

## IA Local (LLaMA) e modelo ONNX

A mesma biblioteca instala os modelos locais que o NXPilot e o NXProject usam, sem script e na mesma pasta para os dois produtos:

- **`LocalAiInstaller`** — IA Local LLaMA: o backend de CPU do LLamaSharp (`llama.dll` e `ggml*.dll`, extraídas do pacote `LLamaSharp.Backend.Cpu` do NuGet, variante AVX2) e o modelo **Qwen3-4B-Instruct-2507** (Apache-2.0, ~2,5 GB) do Hugging Face. A pasta é a configurada no NXProject (`local-ai.json`) ou `C:\IA_LOCAL`. Antes de baixar, confere o espaço livre. O modelo vai para um `.tmp` e só vira `.gguf` depois de conferido: tamanho, **SHA-256 publicado pelo Hugging Face** (lido com um HEAD sem seguir o redirecionamento para a CDN, onde o cabeçalho se perderia) e assinatura GGUF. Grava `backend-version.txt` e `backend-kind.txt`, como o NXProject.
- **`OnnxModelInstaller`** — modelo de embeddings **all-MiniLM-L6-v2** (~90 MB) em `C:\IA_LOCAL\ONNX`.

O que já está instalado não é baixado de novo.

```csharp
var ia = new LocalAiInstaller();
if (!ia.Check().Installed)
    await ia.InstallAsync(progress: new Progress<LocalAiProgress>(p => Console.WriteLine($"{p.Message} {p.Percent:0}%")));

await new OnnxModelInstaller().InstallAsync();
```
