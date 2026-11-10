; Instalador do DesktopFolders (Inno Setup 6). Gerar com: Criar-setup.ps1
#define AppName "DesktopFolders"
#ifndef AppVersion
  #define AppVersion "1.1.0"
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
Name: "sistema"; Description: "Ativar o menu Novo > Pasta inteligente e a miniatura do ícone (pede permissão de administrador uma vez)"
Name: "startup"; Description: "Iniciar o DesktopFolders com o Windows (somente a camada de ícones livres)"; Flags: unchecked

[Files]
Source: "app\DesktopFolders.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "app\DfThumb.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "Instalar-miniatura.cmd"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\DesktopFolders"; Filename: "{app}\DesktopFolders.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopFolders"; ValueData: """{app}\DesktopFolders.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; registra para o usuário atual: "Pasta inteligente", abrir pasta, "Adicionar à pasta inteligente..."
Filename: "{app}\DesktopFolders.exe"; Parameters: "--registrar"; Flags: runhidden waituntilterminated
; opcional: registro da máquina (menu Novo do Windows 11 e miniatura) - abre o aviso de administrador do Windows
Filename: "{app}\DesktopFolders.exe"; Parameters: "--registrar-miniatura --silencioso"; Verb: "runas"; Flags: shellexec waituntilterminated; Tasks: sistema; StatusMsg: "Aguardando a permissão de administrador..."
Filename: "{app}\DesktopFolders.exe"; Description: "Abrir o DesktopFolders agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\DesktopFolders.exe"; Parameters: "--remover-registro"; Flags: runhidden waituntilterminated; RunOnceId: "RemoverRegistroUsuario"
Filename: "{app}\DesktopFolders.exe"; Parameters: "--remover-miniatura --silencioso"; Verb: "runas"; Flags: shellexec waituntilterminated; RunOnceId: "RemoverRegistroMaquina"; Check: IntegracaoDaMaquinaAtiva

[Code]
function IntegracaoDaMaquinaAtiva: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Classes\.0pasta');
end;
