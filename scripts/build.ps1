$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $commitId = git rev-parse --short HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read commit ID' }
    dotnet publish 'src/MaximizeToVirtualDesktop/MaximizeToVirtualDesktop.csproj' `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        "-p:Version=0.1.0-dev.$commitId" -o 'artifacts/win-x64'
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item -LiteralPath 'README.md' -Destination 'artifacts/win-x64/README.md' -ErrorAction Stop
    Copy-Item -LiteralPath 'docs/DEVELOPMENT.zh-CN.md' -Destination 'artifacts/win-x64/DEVELOPMENT.zh-CN.md' -ErrorAction Stop
    $packagePath = "artifacts/MaximizeToVirtualDesktop-dev-$commitId-win-x64.zip"
    if (Test-Path -LiteralPath $packagePath) { throw "Package already exists: $packagePath" }
    Compress-Archive -Path 'artifacts/win-x64/*' -DestinationPath $packagePath -ErrorAction Stop
    Get-Item -LiteralPath $packagePath | Select-Object FullName, Length
}
finally { Pop-Location }
