[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\TrainerStudio.App\TrainerStudio.App.csproj'
$testProject = Join-Path $projectRoot 'tests\TrainerStudio.Core.Tests\TrainerStudio.Core.Tests.csproj'
$publishDirectory = Join-Path $projectRoot 'artifacts\TrainerStudio-win-x64'

Push-Location $projectRoot
try {
    Write-Host 'Kontrollerer .NET SDK ...'
    dotnet --version

    Write-Host 'Kører kernetestene ...'
    dotnet run --project $testProject -c Release

    Write-Host 'Publicerer selvstændig Windows x64-udgave ...'
    dotnet publish $appProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDirectory

    $exe = Join-Path $publishDirectory 'TrainerStudio.exe'
    if (-not (Test-Path $exe)) {
        throw "Den forventede EXE blev ikke fundet: $exe"
    }

    $hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
    $hashFile = "$exe.sha256"
    Set-Content -Path $hashFile -Value "$hash  TrainerStudio.exe" -Encoding ascii

    Write-Host ''
    Write-Host "Færdig: $exe"
    Write-Host "Kontrolsum: $hashFile"
    Write-Host 'Start EXE-filen på Windows og gennemfør det korte testforløb i README.md.'
}
finally {
    Pop-Location
}
