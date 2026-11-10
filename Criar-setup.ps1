# Gera o DesktopFolders-Setup-<versão>.exe (Inno Setup), autossuficiente (inclui o .NET).
# Requer: .NET 8 SDK e Inno Setup 6 (winget install JRSoftware.InnoSetup)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$inst   = Join-Path $root 'Instalador'
$app    = Join-Path $inst 'app'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
$iscc   = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup).' }

Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app | Out-Null
& $dotnet publish (Join-Path $root 'DesktopFolders.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $app
if ($LASTEXITCODE -ne 0) { throw 'Falha no publish' }
& (Join-Path $root 'Thumb\build-thumb.ps1') -Out $app
Remove-Item (Join-Path $app '*.pdb') -Force -ErrorAction SilentlyContinue

$version = ([xml](Get-Content (Join-Path $root 'DesktopFolders.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
& $iscc "/DAppVersion=$version" (Join-Path $inst 'DesktopFolders.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador' }
Get-ChildItem (Join-Path $inst 'saida') -Filter *.exe | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
