param([switch]$UiSmoke)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $root 'AgentS.slnx')
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
& dotnet test (Join-Path $root 'tests/AgentS.Tests/AgentS.Tests.csproj') --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
if ($UiSmoke) {
    $app = Join-Path $root 'src/AgentS.Maui/bin/Debug/net10.0-windows10.0.19041.0/win-x64/AgentS.Maui.exe'
    & dotnet run --project (Join-Path $root 'tests/AgentS.UISmoke') --no-build -- $app
    if ($LASTEXITCODE -ne 0) { throw 'Native UI smoke check failed.' }
}
