<#
.SYNOPSIS
Abre o NXPilot-Setup ja compilado (NAO compila nem publica: use build-nxpilot-setup.ps1 para isso).

Serve para atualizar ou instalar os agentes de IA, ou testar o instalador. Se existir a copia local
do app (dist\app\publish-win-x64, criada pelo build-nxpilot-setup.ps1), o Setup instala essa copia
em vez de baixar do GitHub; sem ela, funciona como o Setup publicado. Os agentes sao baixados do
mesmo jeito nos dois casos.

  .\run-nxpilot-setup.ps1
  .\run-nxpilot-setup.ps1 -Configuration Debug
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$SolutionDir = $PSScriptRoot
$Runtime = "win-x64"
$AppPublishDir = Join-Path $SolutionDir "dist\app\publish-$Runtime"

$exe = Join-Path $SolutionDir "NXPilot-Setup\bin\$Configuration\net10.0-windows\$Runtime\NXPilot-Setup.exe"
if (-not (Test-Path $exe)) {
    Write-Host "NXPilot-Setup nao encontrado para $Configuration. Rode antes: .\build-nxpilot-setup.ps1 -SkipPublish" -ForegroundColor Red
    exit 1
}

# Este script NAO compila: avisa se o codigo do Setup mudou depois deste build.
$exeTime = (Get-Item $exe).LastWriteTime
$newest = Get-ChildItem -Path (Join-Path $SolutionDir "NXPilot-Setup"), (Join-Path $SolutionDir "NXData.AgentInstaller") -Recurse -File -Include *.cs, *.xaml, *.csproj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notlike '*_wpftmp*' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt $exeTime.AddSeconds(5)) {
    Write-Host "Atencao: o codigo do Setup mudou depois deste build ($($exeTime.ToString('dd/MM HH:mm'))). Para ver as mudancas: .\build-nxpilot-setup.ps1 -SkipPublish -Run" -ForegroundColor Yellow
}

if (Test-Path (Join-Path $AppPublishDir "NxPilot.exe")) {
    Write-Host "Iniciando NXPilot-Setup (instala a copia local do app: $AppPublishDir)..." -ForegroundColor Cyan
    & $exe --payload $AppPublishDir
} else {
    Write-Host "Iniciando NXPilot-Setup..." -ForegroundColor Cyan
    & $exe
}
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0 -and $null -ne $exitCode) {
    Write-Host "Setup encerrou com codigo $exitCode." -ForegroundColor Yellow
}
