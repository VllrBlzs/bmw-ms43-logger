[CmdletBinding()]
param(
    [string]$RepositoryRoot = "C:\Users\Valler\Desktop\Biznisz\BMW"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Set-Location $root

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE"
    }
}

$solution = Join-Path $root "BmwMs43Logger.sln"
$projects = @(
    ".\src\BmwMs43Logger.Ediabas\BmwMs43Logger.Ediabas.csproj",
    ".\tools\BmwMs43Logger.RecoveryProbe\BmwMs43Logger.RecoveryProbe.csproj",
    ".\tools\BmwMs43Logger.LiveAcceptanceProbe\BmwMs43Logger.LiveAcceptanceProbe.csproj",
    ".\tools\BmwMs43Logger.RoadTestAnalyzer\BmwMs43Logger.RoadTestAnalyzer.csproj"
)

foreach ($project in $projects) {
    & dotnet sln $solution add $project
    if ($LASTEXITCODE -ne 0) {
        throw "Could not add project: $project"
    }
}

$thirdParty = ".\third_party\ediabaslib\EdiabasLib\EdiabasLib\EdiabasLib.csproj"
$solutionText = [IO.File]::ReadAllText($solution)
if ($solutionText -match [regex]::Escape("third_party\ediabaslib")) {
    & dotnet sln $solution remove $thirdParty
    if ($LASTEXITCODE -ne 0) {
        throw "Could not remove the transitive EdiabasLib project from the solution."
    }
}

Invoke-Checked `
    -FilePath "dotnet" `
    -Arguments @("build", $solution, "-c", "Release") `
    -FailureMessage "BMW-14 solution build failed."

$expectedBackend = "A4DA410DA21D4F1623593EC01D7153FD47306B0C9B59B8FBA3A51F36EC2B3D59"
$framework = "net10.0-windows10.0.26100.0"
$sourceDll = Join-Path $root "third_party\ediabaslib\EdiabasLib\EdiabasLib\bin\Release\$framework\EdiabasLib.dll"

if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf) -or
    (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash -cne $expectedBackend) {
    throw "A rögzített EdiabasLib Release DLL hiányzik vagy hibás."
}

$projectFolders = @(
    "src\BmwMs43Logger.Ediabas",
    "src\BmwMs43Logger.App",
    "tools\BmwMs43Logger.RecoveryProbe",
    "tools\BmwMs43Logger.LiveAcceptanceProbe"
)

foreach ($folder in $projectFolders) {
    $target = Join-Path $root "$folder\bin\Release\$framework\EdiabasLib.dll"
    if (-not (Test-Path -LiteralPath (Split-Path $target -Parent) -PathType Container)) {
        throw "Hiányzó build-kimenet: $target"
    }
    Copy-Item -LiteralPath $sourceDll -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $expectedBackend) {
        throw "A DLL-másolat hash-ellenőrzése sikertelen: $target"
    }
}

Write-Host "BMW14_PINNED_RELEASE_COPIES=PASS"


Invoke-Checked `
    -FilePath "dotnet" `
    -Arguments @(
        "run", "--project",
        ".\tools\BmwMs43Logger.RecoveryProbe\BmwMs43Logger.RecoveryProbe.csproj",
        "-c", "Release", "--no-build", "--",
        "--repo-root", $root
    ) `
    -FailureMessage "BMW-14 offline recovery probe failed."

Invoke-Checked `
    -FilePath "dotnet" `
    -Arguments @(
        "run", "--project",
        ".\tools\BmwMs43Logger.LiveAcceptanceProbe\BmwMs43Logger.LiveAcceptanceProbe.csproj",
        "-c", "Release", "--no-build", "--",
        "--preflight",
        "--ecu-path", "C:\EDIABAS\ECU",
        "--port", "COM5"
    ) `
    -FailureMessage "BMW-14 pinned live preflight failed."

$regressions = @(
    ".\tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj",
    ".\tools\BmwMs43Logger.ParameterManagerProbe\BmwMs43Logger.ParameterManagerProbe.csproj",
    ".\tools\BmwMs43Logger.LiveValueProbe\BmwMs43Logger.LiveValueProbe.csproj",
    ".\tools\BmwMs43Logger.LivePlotProbe\BmwMs43Logger.LivePlotProbe.csproj"
)

foreach ($project in $regressions) {
    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @("run", "--project", $project, "-c", "Release", "--no-build") `
        -FailureMessage "Regression probe failed: $project"
}

$fixture = Get-ChildItem (Join-Path $root "data\private") `
    -Filter "session.meta.json" -File -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object {
        try {
            $metadata = Get-Content $_.FullName -Raw | ConvertFrom-Json
            if ($metadata.status -eq "completed" -and
                [double]$metadata.duration_seconds -ge 840 -and
                @($metadata.selected_parameter_ids).Count -eq 28) {
                [PSCustomObject]@{
                    Directory = $_.Directory.FullName
                    Started = [datetimeoffset]$metadata.started_at_utc
                }
            }
        }
        catch {
            # Ignore unrelated or incomplete private sessions.
        }
    } |
    Sort-Object Started -Descending |
    Select-Object -First 1

if ($null -ne $fixture) {
    Invoke-Checked `
        -FilePath "dotnet" `
        -Arguments @(
            "run", "--project",
            ".\tools\BmwMs43Logger.RoadTestAnalyzer\BmwMs43Logger.RoadTestAnalyzer.csproj",
            "-c", "Release", "--no-build", "--",
            "--run-directory", $fixture.Directory,
            "--expected-parameters", "28",
            "--minimum-seconds", "840",
            "--fixture"
        ) `
        -FailureMessage "BMW-13 historical replay fixture failed."
    Write-Host "BMW13_REPLAY_FIXTURE=PASS"
    Write-Host "BMW13_OFFICIAL_ROAD_TEST=False"
}
else {
    Write-Host "BMW13_REPLAY_FIXTURE=SKIPPED_NO_MATCHING_28_PARAMETER_SESSION"
}

$report = Join-Path $root "artifacts\bmw14\bmw14-offline-recovery-dryrun.json"
if (-not (Test-Path -LiteralPath $report)) {
    throw "BMW-14 report is missing: $report"
}
$roundTrip = Get-Content $report -Raw | ConvertFrom-Json
if ($roundTrip.result -ne "pass" -or $roundTrip.communication_attempted) {
    throw "BMW-14 report round-trip failed."
}

Write-Host "BMW14_REPORT_ROUNDTRIP=PASS"
Write-Host "BMW14_OFFLINE=PASS"
Write-Host "BMW14_LIVE_CAR_VALIDATION_REQUIRED=True"
