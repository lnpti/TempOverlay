; TempOverlay - Inno Setup. Compilar: ISCC.exe installer.iss (depois do build.ps1)
#define AppName    "TempOverlay"
#define AppVersion "1.6.1"
#define AppPublisher "lnpti"
#define AppURL     "https://github.com/lnpti/TempOverlay"
#define AppExe     "TempOverlay.exe"

[Setup]
AppId={{7B1E2C54-3A9D-4F6E-8C21-5D0A9E4B7F33}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={localappdata}\Programs\TempOverlay
DefaultGroupName=TempOverlay
AllowNoIcons=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=TempOverlay_Setup_v{#AppVersion}
SetupIconFile=assets\icon.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "startup"; Description: "Iniciar com o Windows"; GroupDescription: "Opções:"

[Files]
Source: "bin\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TempOverlay"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o TempOverlay agora"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
