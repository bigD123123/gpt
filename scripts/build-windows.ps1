$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'En Windows .exe skal bygges på Windows.' }
& "$PSScriptRoot\install-windows.ps1"
if (-not $?) { throw 'Installation fejlede.' }
Set-Location (Split-Path $PSScriptRoot -Parent)
& .\.venv\Scripts\python.exe -m pip install pyinstaller==6.16.0
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller-installation fejlede.' }
& .\.venv\Scripts\python.exe -m PyInstaller --noconfirm --clean --onefile --console --hide-console hide-early --name AI-Automation-Lab --collect-all keyring --hidden-import keyring.backends.Windows --hidden-import vision_agent.test_game --hidden-import vision_agent.replica_ui run.py
if ($LASTEXITCODE -ne 0) { throw 'Bygning fejlede.' }
& .\dist\AI-Automation-Lab.exe --packaged-smoke
if ($LASTEXITCODE -ne 0) { throw 'Den pakkede test af underproces og IPC fejlede.' }
$hash = (Get-FileHash .\dist\AI-Automation-Lab.exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path .\dist\AI-Automation-Lab.exe.sha256 -Value "$hash  AI-Automation-Lab.exe" -Encoding ascii
Write-Host 'Færdig: dist\AI-Automation-Lab.exe. Pakket underproces/IPC er testet; afprøv også vinduesoptagelse, stop og nøglelager manuelt.'
