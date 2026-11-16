#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "stage\app"
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif

#define MyAppName "LanScreenShare"
#define MyAppPublisher "LanScreenShare"
#define MyAppURL "https://github.com/ginobermudez76/local_streaming"
#define MyAppExeName "LanScreenShare.Desktop.exe"

[Setup]
AppId={{8D6E6B7B-64F1-4D87-A7D2-901F2E8B4B19}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=LanScreenShare-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "firewall"; Description: "Configurar reglas en Windows Defender Firewall (UDP 5000, TCP 5001-5005)"; GroupDescription: "Red y Firewall:"
Name: "firewallpublic"; Description: "Permitir también en redes Públicas (solo si usas Hotspot LAN)"; GroupDescription: "Red y Firewall:"; Flags: unchecked
Name: "installvdd"; Description: "Instalar controlador de Pantalla Inalámbrica (Virtual-Display-Driver MIT)"; GroupDescription: "Modo Pantalla Inalámbrica:"
Name: "uacinteractive"; Description: "Permitir ver e interactuar con diálogos UAC en remoto (sin atenuar al Escritorio Seguro)"; GroupDescription: "Control Remoto UAC (Opcional):"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Configure-LanScreenShareSetup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "VddRootDeviceInstaller.cs.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Configure-LanScreenShareSetup.ps1"" -Action Install -InstallDir ""{app}"" -DownloadDirectory ""{code:GetSelectedDownloadDir}"" -TempWorkDir ""{tmp}\lss-setup""{code:GetSetupOptionSwitches}"; StatusMsg: "Configurando certificado uiAccess, reglas de Firewall y Pantalla Inalámbrica..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser shellexec

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Configure-LanScreenShareSetup.ps1"" -Action Uninstall -InstallDir ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "LanScreenShareCleanup"

[Code]
var
  DownloadDirPage: TInputDirWizardPage;
  DefaultDownloadsPath: string;

procedure InitializeWizard();
begin
  DefaultDownloadsPath := AddBackslash(GetEnv('USERPROFILE')) + 'Downloads\LanScreenShare';
  DownloadDirPage := CreateInputDirPage(
    wpSelectTasks,
    'Carpeta de archivos recibidos',
    'Elige dónde se guardarán los archivos recibidos mediante LanScreenShare.',
    'Las descargas de archivos temporales del instalador usan una carpeta temporal aislada que se limpia al finalizar.' + #13#10 +
    'La carpeta siguiente será la ruta por defecto para transferencias de archivos:',
    False,
    '');
  DownloadDirPage.Add('Carpeta de descargas:');
  DownloadDirPage.Values[0] := DefaultDownloadsPath;
end;

function GetSelectedDownloadDir(Param: string): string;
var
  Chosen: string;
begin
  Chosen := Trim(DownloadDirPage.Values[0]);
  if (Chosen = '') or (CompareText(Chosen, DefaultDownloadsPath) = 0) then
    Result := '{userdownloads}'
  else
    Result := Chosen;
end;

function GetSetupOptionSwitches(Param: string): string;
begin
  Result := '';
  if WizardIsTaskSelected('firewall') then
    Result := Result + ' -ConfigureFirewall';
  if WizardIsTaskSelected('firewall') and WizardIsTaskSelected('firewallpublic') then
    Result := Result + ' -AllowPublicNetwork';
  if WizardIsTaskSelected('installvdd') then
    Result := Result + ' -InstallVirtualDisplayDriver';
  if WizardIsTaskSelected('uacinteractive') then
    Result := Result + ' -AllowInteractiveUac';
end;
