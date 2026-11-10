# Compila o provedor de miniaturas (.NET Framework 4, já incluso no Windows). Uso: .\build-thumb.ps1 <pasta-de-saída>
param([Parameter(Mandatory = $true)][string]$Out)
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force $Out | Out-Null
& "$fw\csc.exe" /nologo /target:library /platform:anycpu /optimize+ "/out:$Out\DfThumb.dll" `
    "/r:$fw\System.Drawing.dll" "/r:$fw\System.Web.Extensions.dll" "$PSScriptRoot\DfThumb.cs"
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar DfThumb.dll" }
"OK: $Out\DfThumb.dll"
