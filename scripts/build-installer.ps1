$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    Write-Host "Running Nocturnal unit tests..."
    dotnet run --project tests/Nocturnal.Tests --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $publishDir = Join-Path $repoRoot 'publish\win-x64'
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    $cliStageDir = Join-Path $repoRoot 'publish\cli-stage'
    if (Test-Path $cliStageDir) {
        Remove-Item -Recurse -Force $cliStageDir
    }

    Write-Host "Publishing self-contained win-x64 binaries..."
    dotnet publish src/Nocturnal.App/Nocturnal.App.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Publishing nocturnal-cli..."
    dotnet publish src/Nocturnal.Cli/Nocturnal.Cli.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -o $cliStageDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($cliFile in @('nocturnal-cli.exe', 'nocturnal-cli.dll', 'nocturnal-cli.deps.json', 'nocturnal-cli.runtimeconfig.json')) {
        $srcFile = Join-Path $cliStageDir $cliFile
        if (-not (Test-Path $srcFile)) {
            throw "Expected CLI output missing: $srcFile"
        }
        Copy-Item $srcFile -Destination $publishDir -Force
    }
    Remove-Item -Recurse -Force $cliStageDir

    $candidates = @(
        (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    $iscc = $candidates | Select-Object -First 1
    if (-not $iscc) {
        throw "ISCC.exe (Inno Setup 6) not found. Install via: winget install JRSoftware.InnoSetup"
    }

    Write-Host "Compiling Windows installer with $iscc..."
    & $iscc (Join-Path $repoRoot 'installer\Nocturnal.iss')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $setupExe = Get-Item (Join-Path $repoRoot 'dist\Nocturnal-Setup-1.3.0.exe')
    $sizeMb = [Math]::Round($setupExe.Length / 1MB, 2)
    Write-Host "Built installer: $($setupExe.FullName) ($sizeMb MB)"
}
finally {
    Pop-Location
}
