# Compila bin\TempOverlay.exe (csc do proprio Windows, sem instalar nada) e gera o instalador dist\TempOverlay_Setup_v<versao>.exe
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force bin | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:assets\icon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:bin\TempOverlay.exe TempOverlay.cs
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
Write-Host "Pronto: bin\ e dist\"
