[CmdletBinding()]
param(
    [string] $EcuPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$expectedCommit = "947dfbbc686290625e3fc9cad044e69af64d8fe9"
$expectedSdk = "10.0.401"
$projectRoot = Split-Path -Parent $PSScriptRoot
$dependencyRoot = Join-Path $projectRoot "third_party\ediabaslib"
$probeProject = Join-Path $projectRoot "tools\BmwMs43Logger.MetadataProbe\BmwMs43Logger.MetadataProbe.csproj"
$outputPath = Join-Path $projectRoot "artifacts\bmw02a"

$sdk = [string] (& dotnet --version)
if ($LASTEXITCODE -ne 0 -or $sdk.Trim() -ne $expectedSdk) {
    throw "Required .NET SDK: $expectedSdk; detected: $($sdk.Trim())"
}

$commit = [string] (& git -C $dependencyRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $commit.Trim() -ne $expectedCommit) {
    throw "EdiabasLib commit mismatch. Required: $expectedCommit; detected: $($commit.Trim())"
}

if ([string]::IsNullOrWhiteSpace($EcuPath)) {
    $EcuPath = @(
        "C:\EDIABAS\ECU",
        "C:\EC-APPS\EDIABAS\ECU"
    ) | Where-Object {
        Test-Path -LiteralPath (Join-Path $_ "ms430ds0.prg")
    } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($EcuPath)) {
    throw "ms430ds0.prg was not found in either known ECU directory."
}

& dotnet run --project $probeProject --configuration Release -- $EcuPath $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "BMW-02A offline metadata probe failed with exit code $LASTEXITCODE."
}
