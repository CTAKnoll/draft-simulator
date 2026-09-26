param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "artifacts/publish/offline-debug-countdown-zoom-win-x64")
)

$ErrorActionPreference = "Stop"

dotnet publish (Join-Path $PSScriptRoot "src/DraftSimulator.App/DraftSimulator.App.csproj") `
    -c Debug `
    -r win-x64 `
    --self-contained true `
    -p:OfflineDebugArtifact=true `
    -p:GenerateSteamAppIdFile=false `
    -o $OutputPath

if ($LASTEXITCODE -ne 0) {
    throw "Offline debug publish failed with exit code $LASTEXITCODE."
}

$debugExe = Join-Path $OutputPath "DraftSimulator.Debug.exe"
if (-not (Test-Path $debugExe)) {
    throw "Offline debug executable was not produced: $debugExe"
}

Write-Host "Offline debug build: $debugExe"
