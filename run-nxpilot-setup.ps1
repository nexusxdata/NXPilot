<#
.SYNOPSIS
Compila e abre o NXPilot-Setup para testar o instalador sem gerar o zip — SO PARA
DESENVOLVIMENTO.

O Setup de verdade (release-nxpilot-setup.ps1) leva so a base e baixa o app do GitHub ao
instalar. Aqui, para testar o Setup com o codigo da maquina (inclusive o que ainda nao foi
publicado), o script publica o app INTEIRO (base + nucleo do NXPilot) em
dist\app\publish-win-x64 e abre o Setup com --payload nessa pasta: com o NxPilot.exe ao
lado, o Setup instala essa copia local em vez de baixar. Pule o publish com -SkipPublish
se nada mudou no app.

  .\run-nxpilot-setup.ps1
  .\run-nxpilot-setup.ps1 -SkipPublish
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipPublish
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

if (-not $SkipPublish -or -not (Test-Path (Join-Path $AppPublishDir "NxPilot.exe"))) {
    Write-Host "Teste local: publicando o app completo ($Runtime) para o Setup instalar esta copia, sem baixar do GitHub..." -ForegroundColor Cyan
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

$exe = Join-Path $SolutionDir "NXPilot-Setup\bin\$Configuration\net10.0-windows\$Runtime\NXPilot-Setup.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Executavel NXPilot-Setup nao encontrado: $exe" -ForegroundColor Red
    exit 1
}

Write-Host "Iniciando NXPilot-Setup em modo de teste (app local: $AppPublishDir)..." -ForegroundColor Cyan
& $exe --payload $AppPublishDir
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0 -and $null -ne $exitCode) {
    Write-Host "Setup encerrou com codigo $exitCode." -ForegroundColor Yellow
}
