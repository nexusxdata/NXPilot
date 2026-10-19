<#
.SYNOPSIS
Publica uma versao nova da biblioteca NXData.AgentInstaller no nuget.org.

A publicacao em si e feita pelo GitHub (workflow .github/workflows/nuget.yml, Trusted Publishing:
sem chave guardada na maquina). Este script prepara e dispara:

  1. confere: arvore limpa, branch main, versao ainda nao publicada no nuget.org
  2. build + testes criticos (build-nxpilot.ps1) e dotnet pack local (confere o conteudo do pacote)
  3. grava a versao no NXData.AgentInstaller.csproj, commit "NXData.AgentInstaller vX.Y.Z" e push
  4. tag nxdata-agentinstaller-vX.Y.Z + push -> o workflow publica no nuget.org
  5. acompanha o workflow (gh) ate o fim e mostra o link do pacote

  .\release-nxdata-agentinstaller.ps1                  # patch: 1.1.0 -> 1.1.1
  .\release-nxdata-agentinstaller.ps1 -Bump minor      # 1.1.0 -> 1.2.0 (API nova)
  .\release-nxdata-agentinstaller.ps1 -Version 2.0.0   # versao exata
  .\release-nxdata-agentinstaller.ps1 -SkipTests       # so quando os testes acabaram de passar

A versao da biblioteca e propria (nao e a do app NXPilot). O NXPilot e o NXProject usam a
biblioteca pelo codigo-fonte (projeto/submodulo); o nuget.org e para quem usa so o pacote.
#>
param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch",
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$SolutionDir = $PSScriptRoot
$PackageId = "NXData.AgentInstaller"
$Csproj = Join-Path $SolutionDir "NXData.AgentInstaller\NXData.AgentInstaller.csproj"
$TagPrefix = "nxdata-agentinstaller-v"

function Write-Step($msg) { Write-Host ""; Write-Host ">> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

# ── 1. Conferencias ──────────────────────────────────────────────────────────
Write-Step "Conferindo o repositorio..."
if (git -C $SolutionDir status --porcelain --untracked-files=no) { Fail "Ha alteracoes nao commitadas. Commite (ou descarte) antes de publicar a biblioteca." }
$branch = git -C $SolutionDir rev-parse --abbrev-ref HEAD
if ($branch -ne "main") { Fail "Publique a partir do main (branch atual: $branch)." }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "GitHub CLI (gh) nao encontrado: ele acompanha o workflow de publicacao." }
git -C $SolutionDir fetch -q --tags origin

$content = Get-Content $Csproj -Raw
if ($content -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { Fail "Nao achei <Version>X.Y.Z</Version> em $Csproj." }
$current = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Versao invalida: $Version (use X.Y.Z)." }
    $new = $Version
} else {
    $ma = [int]$Matches[1]; $mi = [int]$Matches[2]; $pa = [int]$Matches[3]
    $new = switch ($Bump) { "major" { "$($ma + 1).0.0" } "minor" { "$ma.$($mi + 1).0" } default { "$ma.$mi.$($pa + 1)" } }
}

# Versao ja publicada no nuget.org nao pode ser reenviada (o push seria ignorado como duplicado).
$published = @()
try {
    $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLowerInvariant())/index.json"
    $published = @($index.versions)
} catch { Write-Host "  (nao consegui listar as versoes no nuget.org; sigo assim mesmo)" -ForegroundColor DarkGray }
if ($published -contains $new) { Fail "$PackageId $new ja esta no nuget.org. Escolha outra versao (-Bump ou -Version)." }
if (git -C $SolutionDir tag -l "$TagPrefix$new") { Fail "A tag $TagPrefix$new ja existe." }
Write-Host "  $PackageId $current -> $new  (publicadas: $(if ($published) { $published -join ', ' } else { 'nenhuma lida' }))"

# ── 2. Build, testes e pacote local ─────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step "Build + testes criticos (build-nxpilot.ps1)..."
    & (Join-Path $SolutionDir "build-nxpilot.ps1")
    if ($LASTEXITCODE -ne 0) { Fail "Build ou testes falharam: nada foi publicado." }
}

Write-Step "Gerando o pacote local para conferir..."
$packDir = Join-Path $SolutionDir "dist\nuget"
if (Test-Path $packDir) { Remove-Item $packDir -Recurse -Force }
& dotnet pack $Csproj -c Release -o $packDir -p:Version=$new -v q --nologo
if ($LASTEXITCODE -ne 0) { Fail "dotnet pack falhou." }
$nupkg = Join-Path $packDir "$PackageId.$new.nupkg"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
$entries = @($zip.Entries.FullName)
$zip.Dispose()
foreach ($required in @("lib/net10.0/$PackageId.dll", "README.md", "LICENSE.txt", "icon.png")) {
    if ($entries -notcontains $required) { Fail "O pacote nao tem $required." }
}
Write-Host "  OK: $nupkg ($([math]::Round((Get-Item $nupkg).Length / 1KB)) KB)"

# ── 3. Versao no csproj, commit e push ──────────────────────────────────────
Write-Step "Gravando a versao $new e enviando..."
$content = $content -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$new</Version>"
[IO.File]::WriteAllText($Csproj, $content, (New-Object Text.UTF8Encoding($false)))
git -C $SolutionDir add -- $Csproj
git -C $SolutionDir commit -q -m "$PackageId v$new"
if ($LASTEXITCODE -ne 0) { Fail "Commit da versao falhou." }
git -C $SolutionDir push -q origin main
if ($LASTEXITCODE -ne 0) { Fail "Push do main falhou: a tag nao foi criada." }

# ── 4. Tag: dispara o workflow ──────────────────────────────────────────────
Write-Step "Tag $TagPrefix$new (dispara a publicacao no nuget.org)..."
git -C $SolutionDir tag -a "$TagPrefix$new" -m "$PackageId $new"
git -C $SolutionDir push -q origin "$TagPrefix$new"
if ($LASTEXITCODE -ne 0) { Fail "Push da tag falhou." }

# ── 5. Acompanha o workflow ─────────────────────────────────────────────────
Write-Step "Acompanhando o workflow nuget..."
$runId = $null
for ($i = 0; $i -lt 20 -and -not $runId; $i++) {
    Start-Sleep -Seconds 3
    $runId = gh run list --workflow nuget.yml --branch "$TagPrefix$new" --limit 1 --json databaseId -q ".[0].databaseId" 2>$null
}
if (-not $runId) { Fail "Nao encontrei o workflow da tag. Confira em https://github.com/nexusxdata/NXPilot/actions" }
gh run watch $runId --exit-status | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "O workflow falhou: https://github.com/nexusxdata/NXPilot/actions/runs/$runId" }

Write-Host ""
Write-Host "$PackageId $new publicado!" -ForegroundColor Green
Write-Host "  https://www.nuget.org/packages/$PackageId/$new"
Write-Host "  (o nuget.org leva alguns minutos para indexar a versao nova)" -ForegroundColor DarkGray
