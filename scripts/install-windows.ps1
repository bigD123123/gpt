$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        py -3.12 -m venv .venv
    } elseif (Get-Command python -ErrorAction SilentlyContinue) {
        python -m venv .venv
    } else {
        throw 'Installér 64-bit Python 3.12 med Tcl/Tk fra python.org, og prøv igen.'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Installér Python 3.12 med Tcl/Tk fra python.org, og prøv igen.' }
}
& .\.venv\Scripts\python.exe -c "import struct, sys, tkinter; assert sys.version_info[:2] == (3, 12), sys.version; assert struct.calcsize('P') == 8, 'Python must be 64-bit'; root=tkinter.Tk(); root.withdraw(); root.update(); root.destroy()"
if ($LASTEXITCODE -ne 0) {
    throw 'Det eksisterende .venv skal bruge 64-bit Python 3.12 med Tcl/Tk. Flyt .venv og kør scriptet igen.'
}
& .\.venv\Scripts\python.exe -m pip install -r requirements.txt
if ($LASTEXITCODE -ne 0) { throw 'Installation af afhængigheder fejlede.' }
& .\.venv\Scripts\python.exe -m unittest discover -s tests -v
if ($LASTEXITCODE -ne 0) { throw 'Test fejlede.' }
Write-Host 'Installation færdig. Dobbeltklik på Start.cmd.'
