[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\TrainerStudio.App\TrainerStudio.App.csproj'
$testProject = Join-Path $projectRoot 'tests\TrainerStudio.Core.Tests\TrainerStudio.Core.Tests.csproj'
$themeTestProject = Join-Path $projectRoot 'tests\TrainerStudio.Theme.Tests\TrainerStudio.Theme.Tests.csproj'
$themeTestOutput = Join-Path $projectRoot 'artifacts\theme-tests'
$publishDirectory = Join-Path $projectRoot 'artifacts\TrainerStudio-win-x64'

Push-Location $projectRoot
try {
    Write-Host 'Kontrollerer .NET SDK ...'
    dotnet --version

    Write-Host 'Kører kernetestene ...'
    dotnet run --project $testProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Kernetestene fejlede. Udgaven bliver ikke publiceret.' }

    Write-Host 'Tester streaming-scanner og stabile referencer ...'
    dotnet run --project .\tests\TrainerStudio.Scanner.Tests\TrainerStudio.Scanner.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Scanner-testene fejlede.' }
    dotnet run --project .\tests\TrainerStudio.Reference.Tests\TrainerStudio.Reference.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Reference-testene fejlede.' }

    Write-Host 'Tester reel adgang til en almindelig testproces ...'
    dotnet run --project .\tests\TrainerStudio.Windows.Tests\TrainerStudio.Windows.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows-procestestene fejlede.' }

    Write-Host 'Kontrollerer mørkt tema på Windows ...'
    dotnet run --project $themeTestProject -c Release -- $themeTestOutput
    if ($LASTEXITCODE -ne 0) { throw 'Windows-tematestene fejlede. Se billederne i artifacts\theme-tests.' }

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
    if ($LASTEXITCODE -ne 0) { throw 'Windows-publiceringen fejlede.' }

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
