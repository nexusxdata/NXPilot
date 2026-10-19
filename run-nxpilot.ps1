<#
.SYNOPSIS
Abre o NXPilot ja compilado (NAO compila: use build-nxpilot.ps1 -Run para isso).
Sem -Configuration, abre a build mais recente entre Release e Debug.

  .\run-nxpilot.ps1
  .\run-nxpilot.ps1 -Configuration Debug
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration
)

# Remove arquivos temporarios _wpftmp.csproj gerados pelo C# Dev Kit do VS Code.
$wpftmp = Get-ChildItem -Path $PSScriptRoot -Filter "*_wpftmp.csproj" -Recurse -ErrorAction SilentlyContinue
if ($wpftmp) {
    $wpftmp | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "Arquivos temporarios _wpftmp removidos ($($wpftmp.Count))." -ForegroundColor DarkGray
}

function Get-ExePath($configuration) {
    $path = Join-Path $PSScriptRoot "NXPilot.Desktop\bin\$configuration\net10.0-windows\NxPilot.exe"
    if (Test-Path $path) { return $path }
    return $null
}

# O certificado de desenvolvimento e o mesmo do NXProject (sign-nxpilot.ps1): se ele ja
# esta confiavel no perfil, nada a fazer.
function Test-DevCertificateTrusted {
    $certSubject = "CN=NXProject Dev Local"
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $certSubject } |
            Select-Object -First 1

    if (-not $cert) { return $false }

    $store = $null
    try {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "CurrentUser")
        $store.Open("ReadOnly")
        return [bool]($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
    } catch {
        Write-Host "Nao foi possivel verificar CurrentUser\Root: $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    } finally {
        if ($store) { $store.Close() }
    }
}

function Ensure-DevCertificateTrusted {
    if (Test-DevCertificateTrusted) { return }

    $signScript = Join-Path $PSScriptRoot "sign-nxpilot.ps1"
    if (-not (Test-Path $signScript)) {
        Write-Host "Script de assinatura nao encontrado: $signScript" -ForegroundColor Yellow
        return
    }

    Write-Host "Certificado de desenvolvimento nao esta confiavel em CurrentUser\Root. Tentando instalar e assinar..." -ForegroundColor Yellow
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Aviso: assinatura/configuracao do certificado falhou. O aplicativo ainda sera iniciado." -ForegroundColor Yellow
    }
}

if ($PSBoundParameters.ContainsKey('Configuration')) {
    $exe = Get-ExePath $Configuration
    if ($null -eq $exe) {
        Write-Host "Executavel NXPilot nao encontrado para $Configuration. Rode antes: .\build-nxpilot.ps1 -Configuration $Configuration" -ForegroundColor Red
        exit 1
    }
}
else {
    $exe = @(
        Get-ExePath "Release"
        Get-ExePath "Debug"
    ) | Where-Object { $null -ne $_ } |
        Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending |
        Select-Object -First 1

    if ($null -eq $exe) {
        Write-Host "Nenhuma build NXPilot encontrada em Debug ou Release. Rode antes: .\build-nxpilot.ps1" -ForegroundColor Red
        exit 1
    }
}

Ensure-DevCertificateTrusted

# Este script NAO compila. Se o codigo mudou depois do build que vai abrir, avisa: senao a
# tela antiga parece "a mudanca nao funcionou" (aconteceu com a Nova tarefa).
$exeTime = (Get-Item $exe).LastWriteTime
$newest = Get-ChildItem -Path $PSScriptRoot -Recurse -File -Include *.cs, *.xaml, *.csproj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|dist)\\' -and $_.Name -notlike '*_wpftmp*' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTime -gt $exeTime.AddSeconds(5)) {
    Write-Host ""
    Write-Host "Atencao: o codigo mudou depois deste build ($($exeTime.ToString('dd/MM HH:mm')))." -ForegroundColor Yellow
    Write-Host "  Mais recente: $($newest.FullName.Substring($PSScriptRoot.Length + 1)) ($($newest.LastWriteTime.ToString('dd/MM HH:mm')))" -ForegroundColor Yellow
    Write-Host "  Para ver as mudancas: .\build-nxpilot.ps1 -Run  (fecha o NXPilot, compila, testa e abre)" -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "Iniciando NXPilot..." -ForegroundColor Cyan
# Chamada direta (nao Start-Process): o script espera o app fechar e mostra o codigo de saida.
& $exe
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0 -and $null -ne $exitCode) {
    Write-Host "App encerrou com codigo $exitCode." -ForegroundColor Yellow
}
