# Gera dist\TempOverlay.exe e o instalador dist\TempOverlay_Setup_v<versao>.exe
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
python -m PyInstaller --noconfirm --onefile --windowed --name TempOverlay --icon assets\icon.ico tempoverlay.py
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
Write-Host "Pronto: dist\"
