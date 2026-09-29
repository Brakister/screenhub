; ScreenLab - instalador (Inno Setup 7)
; Compilar: "ISCC.exe installer\ScreenLab.iss"
; O exe publicado ja embute modelos (Data/) — o instalador so carrega um
; arquivo. Instalacao por usuario (nao pede admin), destina a outro PC.

#define MyAppName "ScreenLab"
#define MyAppVersion "1.4.0"
#define MyAppExeName "ScreenLab.exe"

[Setup]
AppId={{d6bf0a04-6a87-4f64-ada1-66abdb8e570b}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=ScreenLab
DefaultDirName={localappdata}\Programs\ScreenLab
DefaultGroupName=ScreenLab
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=ScreenLab-Setup-{#MyAppVersion}
Compression=lzma2/ultra
SolidCompression=yes
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
AlwaysShowDirOnReadyPage=yes
ShowLanguageDialog=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Default.isl,compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Area de Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\publish\ScreenLab.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Rodar o {#MyAppName} agora"; Flags: nowait postinstall skipifsilent