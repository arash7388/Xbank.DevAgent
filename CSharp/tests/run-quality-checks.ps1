[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$results = Join-Path $projectRoot 'artifacts/quality-checks'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$summary = [ordered]@{ configuration = $Configuration; build = 'not-run'; dotnet = 'not-run'; ui = 'not-run'; cli = 'not-run' }

Push-Location $projectRoot
try {
    # Release avoids the Debug DLLs commonly held open by Visual Studio.
    & dotnet build Xbank.DevAgent.slnx -c $Configuration --verbosity minimal
    $summary.build = $LASTEXITCODE
    if ($summary.build -eq 0) {
        & dotnet test tests/Xbank.DevAgent.Tests/Xbank.DevAgent.Tests.csproj -c $Configuration --no-build --no-restore `
            --collect:'XPlat Code Coverage' --results-directory $results --logger 'trx;LogFileName=tests.trx' --verbosity minimal
        $summary.dotnet = $LASTEXITCODE
    }

    # Run independent checks even when regression tests have found defects.
    & node tests/ui/run-tests.cjs --report (Join-Path $results 'ui-results.json')
    $summary.ui = $LASTEXITCODE
    if ($summary.build -eq 0) {
        & node tests/cli/cli-contract.test.cjs $Configuration
        $summary.cli = $LASTEXITCODE
    }
}
finally {
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding UTF8
    Pop-Location
}

$summary | Format-Table -AutoSize
Write-Output "Test reports and coverage: $results"
if (@($summary.build, $summary.dotnet, $summary.ui, $summary.cli | Where-Object { $_ -ne 0 }).Count -gt 0) { exit 1 }
