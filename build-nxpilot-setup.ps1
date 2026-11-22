<#
.SYNOPSIS
Compila o NXPilot-Setup para testar o instalador — SO PARA DESENVOLVIMENTO (nada vai para o GitHub).

O Setup de verdade (release-nxpilot-setup.ps1) leva so a base e baixa o app do GitHub ao instalar.
Para testar a instalacao com o codigo da maquina (inclusive o que ainda nao foi publicado), este
script publica o app INTEIRO (base + nucleo do NXPilot) numa pasta LOCAL, dist\app\publish-win-x64
(fora do git), e o run-nxpilot-setup.ps1 abre o Setup apontando para ela. Para so atualizar os
agentes de IA, a publicacao nao faz falta: use -SkipPublish.

  .\build-nxpilot-setup.ps1                 # publica o app na pasta local e compila o Setup
  .\build-nxpilot-setup.ps1 -SkipPublish    # so compila o Setup
  .\build-nxpilot-setup.ps1 -Run            # compila e abre (run-nxpilot-setup.ps1)
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipPublish,
    [switch]$Run
)

$SolutionDir = $PSScriptRoot
$Runtime = "win-x64"
$AppPublishDir = Join-Path $SolutionDir "dist\app\publish-$Runtime"

# Remove arquivos temporarios _wpftmp.csproj gerados pelo C# Dev Kit do VS Code.
Get-ChildItem -Path $SolutionDir -Filter "*_wpftmp.csproj" -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# O exe fica travado enquanto o instalador esta aberto, e ai o build falha com MSB3027.
$processes = Get-Process -Name "NXPilot-Setup" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "Encerrando NXPilot-Setup em execucao..." -ForegroundColor DarkGray
    $processes | Stop-Process -Force
    $processes | ForEach-Object { $_.WaitForExit(5000) | Out-Null }
}

if (-not $SkipPublish) {
    Write-Host "Publicando o app completo ($Runtime) na pasta local $AppPublishDir (para o Setup instalar esta copia)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $SolutionDir "NXPilot.Desktop\NXPilot.Desktop.csproj") -c $Configuration -r $Runtime --self-contained true -o $AppPublishDir -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish do NXPilot falhou." -ForegroundColor Red
        exit 1
    }
}

Write-Host "Compilando NXPilot-Setup ($Configuration)..." -ForegroundColor Cyan
& dotnet build (Join-Path $SolutionDir "NXPilot-Setup\NXPilot-Setup.csproj") -c $Configuration -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build do NXPilot-Setup falhou." -ForegroundColor Red
    exit 1
}
Write-Host "NXPilot-Setup compilado." -ForegroundColor Green

if ($Run) {
    & (Join-Path $SolutionDir "run-nxpilot-setup.ps1") -Configuration $Configuration
}
