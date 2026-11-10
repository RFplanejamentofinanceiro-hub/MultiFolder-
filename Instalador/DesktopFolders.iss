; Instalador do DesktopFolders (Inno Setup 6). Gerar com: Criar-setup.ps1
#define AppName "DesktopFolders"
#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

[Setup]
AppId={{B3D0C0E2-6F7A-4C1B-9A55-2D6E5C0F1A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DesktopFolders
DefaultDirName={localappdata}\Programs\DesktopFolders
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=saida
OutputBaseFilename=DesktopFolders-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\DesktopFolders.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=DesktopFolders.SingleInstance
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startup"; Description: "Iniciar o DesktopFolders com o Windows (necessário para soltar um ícone sobre outro na Área de Trabalho)"

[Files]
Source: "app\DesktopFolders.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\DesktopFolders"; Filename: "{app}\DesktopFolders.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopFolders"; ValueData: """{app}\DesktopFolders.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; registra os itens do botão direito ("Nova pasta inteligente", "Adicionar à pasta inteligente...") só para o usuário atual
Filename: "{app}\DesktopFolders.exe"; Parameters: "--registrar"; Flags: runhidden waituntilterminated
; converte pastas .0pasta das versões 1.1.x em atalhos (que o Windows 11 abre por duplo clique)
Filename: "{app}\DesktopFolders.exe"; Parameters: "--migrar"; Flags: runhidden waituntilterminated
Filename: "{app}\DesktopFolders.exe"; Description: "Abrir o DesktopFolders agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\DesktopFolders.exe"; Parameters: "--remover-registro"; Flags: runhidden waituntilterminated; RunOnceId: "RemoverRegistroUsuario"
