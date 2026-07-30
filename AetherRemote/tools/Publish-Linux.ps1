param(
    [ValidateSet("linux-x64", "linux-arm64")]
    [string]$Runtime = "linux-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot ".artifacts\publish"
$agentOutput = Join-Path $outputRoot "agent-$Runtime"
$brokerOutput = Join-Path $outputRoot "broker-$Runtime"

dotnet publish `
    (Join-Path $projectRoot "src\AetherRemote.Agent\AetherRemote.Agent.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -o $agentOutput
if ($LASTEXITCODE -ne 0) {
    throw "Agent publish failed."
}

dotnet publish `
    (Join-Path $projectRoot "src\AetherRemote.Broker\AetherRemote.Broker.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -o $brokerOutput
if ($LASTEXITCODE -ne 0) {
    throw "Broker publish failed."
}

Write-Host "Agent:  $agentOutput"
Write-Host "Broker: $brokerOutput"
