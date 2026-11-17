param(
    [string]$Version = "1.0.0",
    [bool]$SelfContained = $true,
    [switch]$SkipVddDownload
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$StageAppDir = Join-Path $PSScriptRoot "stage\app"
$OutputDir = Join-Path $PSScriptRoot "output"
$DesktopProj = Join-Path $RepoRoot "LanScreenShare.Desktop\LanScreenShare.Desktop.csproj"
$WatchdogProj = Join-Path $RepoRoot "LanScreenShare.DisplayWatchdog\LanScreenShare.DisplayWatchdog.csproj"
$SecureHostProj = Join-Path $RepoRoot "LanScreenShare.SecureHost\LanScreenShare.SecureHost.csproj"
$IssScript = Join-Path $PSScriptRoot "LanScreenShare.iss"

$VddDriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip"
$VddDriverSha256 = "E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A"

function Find-InnoSetupCompiler {
    $candidates = @(
        (Get-Command "iscc.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }
    throw "No se encontró Inno Setup 6 (ISCC.exe). Instálalo con: winget install JRSoftware.InnoSetup"
}

function Assert-StagedExecutableExists([string]$ExeName) {
    if (-not (Test-Path (Join-Path $StageAppDir $ExeName))) {
        throw "Falta $ExeName en staging."
    }
}

function Stage-VirtualDisplayDriverPackage {
    if ($SkipVddDownload) {
        Write-Host "[3/4] Omitiendo descarga offline de Drivers\VDD (-SkipVddDownload)." -ForegroundColor Yellow
        return
    }

    Write-Host "[3/4] Descargando paquete firmado Virtual-Display-Driver (MIT) en carpeta temporal..." -ForegroundColor Cyan
    $tempCache = Join-Path $env:TEMP ("LanScreenShare-BuildCache-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempCache -Force | Out-Null
    try {
        $zipPath = Join-Path $tempCache "vdd-driver-only.zip"
        Invoke-WebRequest -Uri $VddDriverUrl -OutFile $zipPath -UseBasicParsing
        $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
        if ($actualHash -ne $VddDriverSha256) {
            throw "Hash SHA-256 inválido para Virtual-Display-Driver ($actualHash)."
        }

        $extractPath = Join-Path $tempCache "extracted"
        Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
        $targetDriversDir = Join-Path $StageAppDir "Drivers\VDD"
        New-Item -ItemType Directory -Path $targetDriversDir -Force | Out-Null
        Copy-Item -Path (Join-Path $extractPath "VirtualDisplayDriver\*") -Destination $targetDriversDir -Force
    } finally {
        Remove-Item -Path $tempCache -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-InstallerBuild {
    Write-Host "[1/4] Limpiando staging anterior..." -ForegroundColor Cyan
    if (Test-Path (Join-Path $PSScriptRoot "stage")) {
        Remove-Item (Join-Path $PSScriptRoot "stage") -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StageAppDir -Force | Out-Null
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    Write-Host "[2/4] Publicando LanScreenShare.Desktop (uiAccess=true), Watchdog y SecureHost (self-contained)..." -ForegroundColor Cyan
    $selfContainedArg = if ($SelfContained) { "true" } else { "false" }
    & dotnet publish $DesktopProj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained $selfContainedArg `
        "-p:UsePackagedUiAccessManifest=true" `
        --output $StageAppDir
    if ($LASTEXITCODE -ne 0) { throw "Falló publicación de LanScreenShare.Desktop." }

    & dotnet publish $WatchdogProj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained $selfContainedArg `
        --output $StageAppDir
    if ($LASTEXITCODE -ne 0) { throw "Falló publicación de LanScreenShare.DisplayWatchdog." }

    & dotnet publish $SecureHostProj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained $selfContainedArg `
        --output $StageAppDir
    if ($LASTEXITCODE -ne 0) { throw "Falló publicación de LanScreenShare.SecureHost." }

    Assert-StagedExecutableExists "LanScreenShare.Desktop.exe"
    Assert-StagedExecutableExists "LanScreenShare.DisplayWatchdog.exe"
    Assert-StagedExecutableExists "LanScreenShare.SecureHost.exe"

    Stage-VirtualDisplayDriverPackage

    Write-Host "[4/4] Compilando instalador con Inno Setup..." -ForegroundColor Cyan
    $iscc = Find-InnoSetupCompiler
    & $iscc "/DMyAppVersion=$Version" "/DPublishDir=$StageAppDir" "/DOutputDir=$OutputDir" $IssScript
    if ($LASTEXITCODE -ne 0) { throw "Falló compilación de Inno Setup." }

    $setupExe = Join-Path $OutputDir "LanScreenShare-Setup.exe"
    Write-Host "`nInstalador generado exitosamente: $setupExe" -ForegroundColor Green
}

Invoke-InstallerBuild
