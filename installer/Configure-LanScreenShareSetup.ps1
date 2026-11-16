[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall", "RepairVdd")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [string]$DownloadDirectory = "",
    [switch]$ConfigureFirewall,
    [switch]$AllowPublicNetwork,
    [switch]$InstallVirtualDisplayDriver,
    [switch]$AllowInteractiveUac,
    [string]$TempWorkDir = ""
)

$ErrorActionPreference = "Stop"

$CertSubject = "CN=LanScreenShare Local Trusted"
$FirewallPrefix = "LanScreenShare - "
$ProgramDataDir = Join-Path $env:ProgramData "LanScreenShare"
$DefaultsJsonPath = Join-Path $ProgramDataDir "install-defaults.json"
$SetupLogPath = Join-Path $ProgramDataDir "setup.log"
$UacPolicyKeyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
$VddDriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip"
$VddDriverSha256 = "E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A"
$UsersSidAcl = "*S-1-5-32-545:(OI)(CI)(M)"

function Write-SetupLog([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    try {
        if (Test-Path $ProgramDataDir) {
            Add-Content -Path $SetupLogPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
        }
    } catch {
        # Best-effort file logging
    }
}
$FirewallPorts = @(
    @{ Name = "Descubrimiento LAN (UDP 5000)"; Protocol = "UDP"; Port = "5000" },
    @{ Name = "Video Stream mTLS (TCP 5001)"; Protocol = "TCP"; Port = "5001" },
    @{ Name = "Audio WASAPI mTLS (TCP 5002)"; Protocol = "TCP"; Port = "5002" },
    @{ Name = "Control Remoto LSI2/LSDC (TCP 5003)"; Protocol = "TCP"; Port = "5003" },
    @{ Name = "Transferencia de Archivos mTLS (TCP 5004)"; Protocol = "TCP"; Port = "5004" },
    @{ Name = "Pantalla Inalámbrica TLS (TCP 5005)"; Protocol = "TCP"; Port = "5005" }
)

function Resolve-TempDirectory {
    if (-not [string]::IsNullOrWhiteSpace($TempWorkDir)) {
        return $TempWorkDir
    }
    return Join-Path $env:TEMP ("LanScreenShare-Setup-" + [Guid]::NewGuid().ToString("N"))
}

function Grant-UsersModifyAcl([string]$Path) {
    if (-not (Test-Path $Path)) {
        return
    }
    & icacls.exe $Path /grant $UsersSidAcl /T /C /Q | Out-Null
}

function Remove-LanScreenShareFirewallRules {
    try {
        Get-NetFirewallRule -ErrorAction Stop |
            Where-Object { $_.DisplayName -like "$FirewallPrefix*" } |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    } catch {
        foreach ($rule in $FirewallPorts) {
            & netsh.exe advfirewall firewall delete rule name="$FirewallPrefix$($rule.Name) [In]" | Out-Null
            & netsh.exe advfirewall firewall delete rule name="$FirewallPrefix$($rule.Name) [Out]" | Out-Null
        }
    }
}

function Install-LanScreenShareFirewallRules([string]$ExePath, [bool]$IncludePublic) {
    Remove-LanScreenShareFirewallRules

    $profiles = if ($IncludePublic) { @("Private", "Public") } else { @("Private") }
    foreach ($rule in $FirewallPorts) {
        New-NetFirewallRule `
            -DisplayName "$FirewallPrefix$($rule.Name) [In]" `
            -Direction Inbound `
            -Action Allow `
            -Enabled True `
            -Profile $profiles `
            -Protocol $rule.Protocol `
            -LocalPort $rule.Port `
            -Program $ExePath | Out-Null

        New-NetFirewallRule `
            -DisplayName "$FirewallPrefix$($rule.Name) [Out]" `
            -Direction Outbound `
            -Action Allow `
            -Enabled True `
            -Profile $profiles `
            -Protocol $rule.Protocol `
            -LocalPort $rule.Port `
            -Program $ExePath | Out-Null
    }
}

function Ensure-TrustedLocalSigningAndSign([string]$ExePath, [string]$ScratchDir) {
    if (-not (Test-Path $ExePath)) {
        throw "No se encontró el ejecutable para firmar: $ExePath"
    }

    $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $cert) {
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $CertSubject `
            -FriendlyName "LanScreenShare Local UiAccess Code Signing" `
            -CertStoreLocation "Cert:\LocalMachine\My" `
            -NotAfter (Get-Date).AddYears(5) `
            -KeyExportPolicy NonExportable
    }

    New-Item -ItemType Directory -Path $ScratchDir -Force | Out-Null
    $cerPath = Join-Path $ScratchDir "LanScreenShareTrusted.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

    try {
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
    } finally {
        Remove-Item -Path $cerPath -Force -ErrorAction SilentlyContinue
    }

    $sig = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256
    if ($sig.Status -ne "Valid") {
        throw "La firma Authenticode falló con estado $($sig.Status): $($sig.StatusMessage)"
    }
}

function Remove-TrustedLocalSigningCerts {
    $stores = @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher")
    foreach ($store in $stores) {
        Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $CertSubject } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

function Configure-SharedDownloadDirectory([string]$RequestedDir) {
    New-Item -ItemType Directory -Path $ProgramDataDir -Force | Out-Null
    Grant-UsersModifyAcl $ProgramDataDir

    $trimmed = if ($null -eq $RequestedDir) { "" } else { $RequestedDir.Trim() }
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed -eq "{userdownloads}") {
        Remove-Item -Path $DefaultsJsonPath -Force -ErrorAction SilentlyContinue
        return
    }

    New-Item -ItemType Directory -Path $trimmed -Force | Out-Null
    Grant-UsersModifyAcl $trimmed

    $payload = @{ DefaultDownloadDirectory = $trimmed } | ConvertTo-Json -Depth 2
    [System.IO.File]::WriteAllText($DefaultsJsonPath, $payload, [System.Text.UTF8Encoding]::new($false))
}

function Find-MttVddDevice {
    return Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
        Where-Object {
            @($_.HardwareId) | Where-Object { $_ -ieq "Root\MttVDD" -or $_ -ieq "MttVDD" }
        } |
        Select-Object -First 1
}

function Register-SingleVddTask($Service, $Folder, [string]$TaskName, [string]$Arguments) {
    $taskDef = $Service.NewTask(0)
    $taskDef.RegistrationInfo.Description = "LanScreenShare Virtual Display Controller"
    $taskDef.Principal.UserId = "NT AUTHORITY\SYSTEM"
    $taskDef.Principal.LogonType = 5
    $taskDef.Principal.RunLevel = 1
    $taskDef.Settings.Enabled = $true
    $taskDef.Settings.AllowDemandStart = $true
    $taskDef.Settings.DisallowStartIfOnBatteries = $false
    $taskDef.Settings.StopIfGoingOnBatteries = $false
    $taskDef.Settings.Hidden = $true

    $action = $taskDef.Actions.Create(0)
    $action.Path = (Join-Path $env:SystemRoot "System32\pnputil.exe")
    $action.Arguments = $Arguments

    $sddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)"
    $Folder.RegisterTaskDefinition($TaskName, $taskDef, 6, "NT AUTHORITY\SYSTEM", $null, 5, $sddl) | Out-Null
}

function Register-VddControlTasks([string]$InstanceId) {
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $rootFolder = $service.GetFolder([string][char]92)
        Register-SingleVddTask $service $rootFolder "LanScreenShare-EnableVdd" "/enable-device `"$InstanceId`""
        Register-SingleVddTask $service $rootFolder "LanScreenShare-DisableVdd" "/disable-device `"$InstanceId`""
        Write-SetupLog "Tareas programadas LanScreenShare-EnableVdd / DisableVdd registradas para $InstanceId."
    } catch {
        Write-SetupLog "Advertencia registrando tareas programadas VDD: $($_.Exception.Message)"
    }
}

function Unregister-VddControlTasks {
    try {
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $rootFolder = $service.GetFolder([string][char]92)
        foreach ($name in @("LanScreenShare-EnableVdd", "LanScreenShare-DisableVdd")) {
            try { $rootFolder.DeleteTask($name, 0) } catch { }
        }
    } catch {
        # Best-effort cleanup
    }
}

function Test-MttVddPipeAvailable {
    return [System.IO.Directory]::GetFiles("\\.\pipe\", "MTTVirtualDisplayPipe").Length -gt 0
}

function Test-MttVddDeviceHealthy($Device) {
    if ($null -eq $Device) {
        return $false
    }
    return $Device.Status -eq "OK"
}

function Wait-ForMttVddReady {
    $deadline = (Get-Date).AddSeconds(4)
    do {
        $dev = Find-MttVddDevice
        if ((Test-MttVddDeviceHealthy $dev) -and (Test-MttVddPipeAvailable)) {
            return $dev
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return Find-MttVddDevice
}

function Complete-VddRegistration($Device, [string]$StatusNote) {
    Register-VddControlTasks -InstanceId $Device.InstanceId
    & pnputil.exe /disable-device $Device.InstanceId | Out-Null
    Write-SetupLog "$StatusNote ($($Device.InstanceId))."
}

function TryUpdateExistingVdd($ExistingDevice, [string]$InfPath) {
    if ($null -eq $ExistingDevice) {
        return $false
    }
    if ($ExistingDevice.Status -ne "OK") {
        & pnputil.exe /enable-device $ExistingDevice.InstanceId | Out-Null
    }
    & pnputil.exe /add-driver $InfPath /install | Out-Null
    $readyDev = Wait-ForMttVddReady
    if ($null -eq $readyDev) {
        return $false
    }
    Complete-VddRegistration $readyDev "Root\MttVDD actualizado y puesto en reposo"
    return $true
}

function Ensure-RootMttVddDeviceNode([string]$InfPath) {
    $existing = Find-MttVddDevice
    if (TryUpdateExistingVdd $existing $InfPath) {
        return
    }

    Write-SetupLog "Registrando dispositivo PnP Root\MttVDD desde $InfPath..."
    $typeSourcePath = Join-Path $PSScriptRoot "VddRootDeviceInstaller.cs.txt"
    $typeDefinition = [System.IO.File]::ReadAllText($typeSourcePath)
    Add-Type -TypeDefinition $typeDefinition
    [VddRootDeviceInstaller]::InstallRootDevice("Root\MttVDD", $InfPath)

    $finalDev = Wait-ForMttVddReady
    if ($null -eq $finalDev) {
        throw "No se detectó el adaptador Root\MttVDD después de instalar el INF."
    }
    $pipeReady = Test-MttVddPipeAvailable
    Write-SetupLog "Root\MttVDD instalado: InstanceId=$($finalDev.InstanceId), Status=$($finalDev.Status), PipeReady=$pipeReady"
    Complete-VddRegistration $finalDev "Root\MttVDD puesto en reposo hasta iniciar Extender pantalla"
}

function Enable-InteractiveUacOnDefaultDesktop {
    if (-not (Test-Path $UacPolicyKeyPath)) {
        New-Item -Path $UacPolicyKeyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $UacPolicyKeyPath -Name "PromptOnSecureDesktop" -Value 0 -Type DWord -Force
    Write-SetupLog "PromptOnSecureDesktop establecido en 0 (UAC en escritorio Default para uiAccess=true)."
}

function Import-CatalogSignersToTrustedPublisher([string]$CatPath, [string]$ScratchDir) {
    if (-not (Test-Path $CatPath)) {
        return
    }
    $bytes = [System.IO.File]::ReadAllBytes($CatPath)
    $collection = [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $collection.Import($bytes)
    $certsOut = Join-Path $ScratchDir "VddSigners"
    New-Item -ItemType Directory -Path $certsOut -Force | Out-Null
    foreach ($signer in $collection) {
        $file = Join-Path $certsOut "$($signer.Thumbprint).cer"
        [System.IO.File]::WriteAllBytes($file, $signer.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Import-Certificate -FilePath $file -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
    }
}

function Install-VirtualDisplayDriverPackage([string]$AppDir, [string]$ScratchDir) {
    $bundledDir = Join-Path $AppDir "Drivers\VDD"
    $bundledInf = Join-Path $bundledDir "MttVDD.inf"
    $sourceDir = $bundledDir

    if (-not (Test-Path $bundledInf)) {
        New-Item -ItemType Directory -Path $ScratchDir -Force | Out-Null
        $zipPath = Join-Path $ScratchDir "vdd-driver.zip"
        Invoke-WebRequest -Uri $VddDriverUrl -OutFile $zipPath -UseBasicParsing
        $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
        if ($actualHash -ne $VddDriverSha256) {
            throw "Hash SHA-256 inválido para VirtualDisplayDriver ($actualHash)."
        }

        $extractDir = Join-Path $ScratchDir "vdd-extracted"
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
        $sourceDir = Join-Path $extractDir "VirtualDisplayDriver"
    }

    $targetVddDir = Join-Path $env:SystemDrive "VirtualDisplayDriver"
    New-Item -ItemType Directory -Path $targetVddDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDir "*") -Destination $targetVddDir -Force

    $settingsXml = Join-Path $targetVddDir "vdd_settings.xml"
    if (Test-Path $settingsXml) {
        $xml = [System.IO.File]::ReadAllText($settingsXml)
        $xml = [System.Text.RegularExpressions.Regex]::Replace($xml, "<count>\d+</count>", "<count>0</count>")
        [System.IO.File]::WriteAllText($settingsXml, $xml, [System.Text.UTF8Encoding]::new($false))
    }
    Grant-UsersModifyAcl $targetVddDir

    Import-CatalogSignersToTrustedPublisher (Join-Path $targetVddDir "mttvdd.cat") $ScratchDir
    Ensure-RootMttVddDeviceNode (Join-Path $targetVddDir "MttVDD.inf")
}

function Initialize-SetupProgramData {
    New-Item -ItemType Directory -Path $ProgramDataDir -Force | Out-Null
    Grant-UsersModifyAcl $ProgramDataDir
}

function Invoke-InstallAction([string]$ScratchDir) {
    Initialize-SetupProgramData
    Write-SetupLog "Iniciando configuración de instalación en '$InstallDir'."

    $exePath = Join-Path $InstallDir "LanScreenShare.Desktop.exe"
    Ensure-TrustedLocalSigningAndSign -ExePath $exePath -ScratchDir $ScratchDir
    Write-SetupLog "Certificado confiable uiAccess y firma Authenticode verificados."

    if ($ConfigureFirewall) {
        Install-LanScreenShareFirewallRules -ExePath $exePath -IncludePublic $AllowPublicNetwork.IsPresent
        Write-SetupLog "Reglas de Firewall configuradas (Public=$($AllowPublicNetwork.IsPresent))."
    }

    Configure-SharedDownloadDirectory -RequestedDir $DownloadDirectory
    Write-SetupLog "Carpeta de descargas configurada ('$DownloadDirectory')."

    if ($InstallVirtualDisplayDriver) {
        Install-VirtualDisplayDriverPackage -AppDir $InstallDir -ScratchDir $ScratchDir
    }
    if ($AllowInteractiveUac) {
        Enable-InteractiveUacOnDefaultDesktop
    }
    Write-SetupLog "Configuración de instalación completada exitosamente."
}

function Invoke-RepairVddAction([string]$ScratchDir) {
    Initialize-SetupProgramData
    Write-SetupLog "Ejecutando reparación/instalación de Virtual-Display-Driver desde '$InstallDir'."
    Install-VirtualDisplayDriverPackage -AppDir $InstallDir -ScratchDir $ScratchDir
    Write-SetupLog "Reparación de Virtual-Display-Driver completada."
}

function Invoke-UninstallAction {
    Get-Process -Name "LanScreenShare.Desktop", "LanScreenShare.DisplayWatchdog" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $existingVdd = Find-MttVddDevice
    if ($null -ne $existingVdd) {
        & pnputil.exe /disable-device $existingVdd.InstanceId | Out-Null
    }
    Unregister-VddControlTasks
    Remove-LanScreenShareFirewallRules
    Remove-TrustedLocalSigningCerts
    Remove-Item -Path $DefaultsJsonPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $SetupLogPath -Force -ErrorAction SilentlyContinue
    if ((Test-Path $ProgramDataDir) -and ((Get-ChildItem $ProgramDataDir -Force).Count -eq 0)) {
        Remove-Item -Path $ProgramDataDir -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SetupEntrypoint {
    $scratch = Resolve-TempDirectory
    try {
        switch ($Action) {
            "Install" { Invoke-InstallAction -ScratchDir $scratch }
            "RepairVdd" { Invoke-RepairVddAction -ScratchDir $scratch }
            "Uninstall" { Invoke-UninstallAction }
        }
    } catch {
        Write-SetupLog "ERROR: $($_.Exception.Message)"
        throw
    } finally {
        Remove-Item -Path $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-SetupEntrypoint
