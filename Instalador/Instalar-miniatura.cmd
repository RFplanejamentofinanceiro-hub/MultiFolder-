@echo off
rem Ativa o menu Novo > Pasta inteligente e a miniatura (pede permissao de administrador uma vez).
rem Para desfazer: Instalar-miniatura.cmd remover
set "EXE=%~dp0DesktopFolders.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\Programs\DesktopFolders\DesktopFolders.exe"
if /i "%1"=="remover" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -ArgumentList '--remover-miniatura' -Verb RunAs"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -ArgumentList '--registrar-miniatura' -Verb RunAs"
)
