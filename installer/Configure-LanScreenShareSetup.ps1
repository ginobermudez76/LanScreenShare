[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall")]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [string]$DownloadDirectory = "",
    [switch]$ConfigureFirewall,
    [switch]$AllowPublicNetwork,
    [switch]$InstallVirtualDisplayDriver,
    [string]$TempWorkDir = ""
)

$ErrorActionPreference = "Stop"

$CertSubject = "CN=LanScreenShare Local Trusted"
$FirewallPrefix = "LanScreenShare - "
$ProgramDataDir = Join-Path $env:ProgramData "LanScreenShare"
$DefaultsJsonPath = Join-Path $ProgramDataDir "install-defaults.json"
$VddDriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip"
$VddDriverSha256 = "E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A"
$UsersSidAcl = "*S-1-5-32-545:(OI)(CI)(M)"
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

function Ensure-RootMttVddDeviceNode([string]$InfPath) {
    $existing = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like "ROOT\MTTVDD*" } |
        Select-Object -First 1
    if ($null -ne $existing) {
        & pnputil.exe /add-driver $InfPath /install | Out-Null
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class VddRootDeviceInstaller
{
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint DICD_GENERATE_ID = 0x00000001;
    private const uint INSTALLFLAG_FORCE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr DeviceInfoSet,
        string DeviceName,
        ref Guid ClassGuid,
        string DeviceDescription,
        IntPtr hwndParent,
        uint CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        byte[] PropertyBuffer,
        int PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string HardwareId,
        string FullInfPath,
        uint InstallFlags,
        out bool bRebootRequired);

    public static void InstallRootDevice(string hardwareId, string infPath)
    {
        Guid displayGuid = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(ref displayGuid, IntPtr.Zero);
        if (devInfoSet == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var devData = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfo(devInfoSet, "MttVDD", ref displayGuid, "Virtual Display Driver", IntPtr.Zero, DICD_GENERATE_ID, ref devData))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            byte[] hwIdBytes = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(devInfoSet, ref devData, SPDRP_HARDWAREID, hwIdBytes, hwIdBytes.Length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoSet, ref devData))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }
}
"@

    [VddRootDeviceInstaller]::InstallRootDevice("Root\MttVDD", $InfPath)
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

$scratch = Resolve-TempDirectory
try {
    if ($Action -eq "Install") {
        $exePath = Join-Path $InstallDir "LanScreenShare.Desktop.exe"
        Ensure-TrustedLocalSigningAndSign -ExePath $exePath -ScratchDir $scratch

        if ($ConfigureFirewall) {
            Install-LanScreenShareFirewallRules -ExePath $exePath -IncludePublic $AllowPublicNetwork.IsPresent
        }

        Configure-SharedDownloadDirectory -RequestedDir $DownloadDirectory

        if ($InstallVirtualDisplayDriver) {
            Install-VirtualDisplayDriverPackage -AppDir $InstallDir -ScratchDir $scratch
        }
    } else {
        Get-Process -Name "LanScreenShare.Desktop", "LanScreenShare.DisplayWatchdog" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue

        Remove-LanScreenShareFirewallRules
        Remove-TrustedLocalSigningCerts
        Remove-Item -Path $DefaultsJsonPath -Force -ErrorAction SilentlyContinue
        if ((Test-Path $ProgramDataDir) -and ((Get-ChildItem $ProgramDataDir -Force).Count -eq 0)) {
            Remove-Item -Path $ProgramDataDir -Force -ErrorAction SilentlyContinue
        }
    }
} finally {
    Remove-Item -Path $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
