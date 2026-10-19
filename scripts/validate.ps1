param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    dotnet build -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test -c Release --no-build --nologo -v minimal --logger 'trx;LogFileName=unit-tests.trx' --results-directory 'artifacts/test-results'
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }
    if ($Integration) {
        if (-not (Test-Path -LiteralPath 'artifacts')) {
            New-Item -ItemType Directory -Path 'artifacts' -ErrorAction Stop | Out-Null
        }
        dotnet run --project 'tests/MaximizeToVirtualDesktop.SmokeTests' -c Release --no-build |
            Tee-Object -FilePath 'artifacts/integration.log'
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed' }
    }
}
finally { Pop-Location }
