[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string]$RoadTestReport,

    [string]$FaultMatrixReport,

    [switch]$UpdateReadme
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($RepositoryRoot)
Set-Location -LiteralPath $root

$offlineScript = Join-Path $root "scripts\test-bmw14-offline.ps1"
$readmePath = Join-Path $root "README.md"
$qaDirectory = Join-Path $root "artifacts\bmw15"
$qaDocumentPath = Join-Path $root "docs\BMW-15_FINAL_QA.md"
New-Item -ItemType Directory -Path $qaDirectory -Force | Out-Null

function Get-LatestPassingJson {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,

        [Parameter(Mandatory)]
        [scriptblock]$Predicate
    )

    $candidates = Get-ChildItem `
        -LiteralPath (Join-Path $root "data\private") `
        -Filter $FileName `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending

    foreach ($candidate in $candidates) {
        try {
            $json = Get-Content -LiteralPath $candidate.FullName -Raw |
                ConvertFrom-Json
            if (& $Predicate $json) {
                return $candidate.FullName
            }
        }
        catch {
            continue
        }
    }
    throw "No passing $FileName evidence was found under data\private."
}

if ([string]::IsNullOrWhiteSpace($RoadTestReport)) {
    $RoadTestReport = Get-LatestPassingJson `
        -FileName "bmw13-road-test-acceptance.json" `
        -Predicate {
            param($json)
            [bool]$json.official_road_test -and $json.result -eq "pass"
        }
}

if ([string]::IsNullOrWhiteSpace($FaultMatrixReport)) {
    $FaultMatrixReport = Get-LatestPassingJson `
        -FileName "bmw14-live-fault-matrix.json" `
        -Predicate {
            param($json)
            $json.result -eq "pass" -and [int]$json.distinct_epochs -eq 5
        }
}

$RoadTestReport = [IO.Path]::GetFullPath($RoadTestReport)
$FaultMatrixReport = [IO.Path]::GetFullPath($FaultMatrixReport)

$road = Get-Content -LiteralPath $RoadTestReport -Raw | ConvertFrom-Json
$matrix = Get-Content -LiteralPath $FaultMatrixReport -Raw | ConvertFrom-Json

$roadPass = (
    [bool]$road.official_road_test -and
    $road.result -eq "pass" -and
    $road.session_status -eq "completed" -and
    [int]$road.epochs -eq 1 -and
    [int]$road.parameters -eq 22 -and
    [double]$road.valid_ratio -ge 0.98 -and
    [double]$road.effective_hz -ge 1.5 -and
    [bool]$road.csv_sha256_match -and
    [bool]$road.schema_and_roundtrip -and
    [bool]$road.coverage_pass -and
    [bool]$road.cadence_pass -and
    [bool]$road.road_dynamics_pass -and
    [bool]$road.privacy_pass
)
if (-not $roadPass) {
    throw "BMW-13 official road-test evidence is incomplete or failed."
}

$expectedPhases = @(
    "baseline",
    "cable_disconnect",
    "reconnect_after_cable",
    "ignition_off",
    "reconnect_after_ignition"
)
$actualPhases = @($matrix.phases | ForEach-Object phase)
$matrixPass = (
    $matrix.result -eq "pass" -and
    [bool]$matrix.read_only_dispatch -and
    $matrix.engine_state -eq "off" -and
    [int]$matrix.phase_count -eq 5 -and
    [int]$matrix.distinct_epochs -eq 5 -and
    ($actualPhases -join "|") -ceq ($expectedPhases -join "|") -and
    @($matrix.phases | Where-Object result -ne "pass").Count -eq 0
)
if (-not $matrixPass) {
    throw "BMW-14 live fault/recovery evidence is incomplete or failed."
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    $offlineScript -RepositoryRoot $root
if ($LASTEXITCODE -ne 0) {
    throw "BMW-15 offline regression gate failed. Exit code: $LASTEXITCODE"
}

dotnet build (Join-Path $root "BmwMs43Logger.sln") `
    -c Release `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "BMW-15 final Release build failed. Exit code: $LASTEXITCODE"
}

git -C $root diff --check
if ($LASTEXITCODE -ne 0) {
    throw "BMW-15 working-tree diff check failed."
}

$invariant = [Globalization.CultureInfo]::InvariantCulture
$duration = ([double]$road.duration_seconds).ToString("F2", $invariant)
$effectiveHz = ([double]$road.effective_hz).ToString("F3", $invariant)
$validPercent = (100.0 * [double]$road.valid_ratio).ToString("F2", $invariant)
$roadHash = (Get-FileHash -LiteralPath $RoadTestReport -Algorithm SHA256).Hash
$matrixHash = (Get-FileHash -LiteralPath $FaultMatrixReport -Algorithm SHA256).Hash

$qaSummary = [ordered]@{
    schema_version = "bmw15.final_qa.v1"
    generated_utc = [DateTimeOffset]::UtcNow
    bmw13 = [ordered]@{
        official_road_test = $true
        duration_seconds = [double]$road.duration_seconds
        parameters = [int]$road.parameters
        cycles = [long]$road.cycles
        rows = [long]$road.rows
        valid_ratio = [double]$road.valid_ratio
        effective_hz = [double]$road.effective_hz
        report_sha256 = $roadHash
        result = "pass"
    }
    bmw14 = [ordered]@{
        engine_state = "off"
        phase_count = 5
        distinct_epochs = 5
        report_sha256 = $matrixHash
        result = "pass"
    }
    offline_regression = "pass"
    release_build = "pass"
    diff_check = "pass"
    result = "pass"
}

$qaJsonPath = Join-Path $qaDirectory "bmw15-final-qa.json"
[IO.File]::WriteAllText(
    $qaJsonPath,
    ($qaSummary | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false)
)

$qaMarkdown = @"
# BMW-15 - final QA and release evidence

## Accepted evidence

- BMW-13 official road test: **PASS**
- Duration: **$duration seconds**
- Approved live parameters: **$($road.parameters)**
- Acquisition cycles: **$($road.cycles)**
- Logged rows: **$($road.rows)**
- Valid rows: **$validPercent%**
- Effective cycle rate: **$effectiveHz Hz**
- CSV hash, schema round-trip, coverage, cadence, road dynamics and privacy: **PASS**
- BMW-14 live fault/recovery matrix: **PASS**
- Fault/recovery phases: **5**
- Distinct acquisition epochs: **5**
- Cable loss/recovery and ignition loss/recovery: **PASS**
- Engine state during BMW-14: **OFF**
- Offline regression gate: **PASS**
- Final Release build: **PASS**
- Working-tree diff check: **PASS**

## Safety boundary

The application is a read-only MS43 data logger. Live dispatch is restricted to
the pinned status-job allowlist with empty arguments. Unknown jobs, unknown
parameters and actuator/write requests are rejected.

## Validated scope

BMW-13 proves the already completed live GUI and real-road logging path. BMW-14
proves bounded transport failure handling, CSV/metadata finalization, interface
release and clean reconnection into new acquisition epochs. BMW-15 adds no new
vehicle test; it closes the regression, documentation and release gates.

## Known limits

- 22 catalog parameters are approved for live use; withheld parameters remain
  excluded until their semantics are separately validated.
- The logger is not a substitute for BMW fault-memory diagnosis or mechanical
  inspection.
- Effective acquisition rate depends on the selected EDIABAS jobs and transport
  latency.

## Evidence hashes

- BMW-13 acceptance report SHA-256: $roadHash
- BMW-14 matrix report SHA-256: $matrixHash
"@

[IO.File]::WriteAllText(
    $qaDocumentPath,
    $qaMarkdown.Trim() + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false)
)

if ($UpdateReadme) {
    if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
        throw "README.md was not found at the repository root."
    }
    $readme = [IO.File]::ReadAllText($readmePath)
    $startMarker = "<!-- BMW15_STATUS_START -->"
    $endMarker = "<!-- BMW15_STATUS_END -->"
    $statusBlock = @"
$startMarker
## Validated project status

- **BMW-13 official road test:** PASS - $duration s, $($road.parameters) approved parameters, $($road.rows) rows, $validPercent% valid, $effectiveHz Hz effective cycle rate.
- **BMW-14 live fault/recovery matrix:** PASS - cable loss/recovery and ignition loss/recovery, five distinct acquisition epochs, engine off.
- **BMW-15 final QA:** PASS - offline regression, Release build and diff check.

The release scope is a read-only BMW MS43 logger. Dispatch is restricted to a
pinned allowlist of status jobs with empty arguments; unknown and actuator/write
requests are rejected. See [BMW-15 final QA](docs/BMW-15_FINAL_QA.md) for evidence and limits.
$endMarker
"@
    $statusBlock = $statusBlock.Trim()

    if ($readme.Contains($startMarker) -and $readme.Contains($endMarker)) {
        $pattern = "(?s)" +
            [Regex]::Escape($startMarker) + ".*?" +
            [Regex]::Escape($endMarker)
        $readme = [Regex]::Replace($readme, $pattern, $statusBlock, 1)
    }
    elseif (-not $readme.Contains($startMarker) -and
            -not $readme.Contains($endMarker)) {
        $readme = $readme.TrimEnd() +
            [Environment]::NewLine + [Environment]::NewLine +
            $statusBlock + [Environment]::NewLine
    }
    else {
        throw "README contains only one BMW-15 status marker."
    }

    [IO.File]::WriteAllText(
        $readmePath,
        $readme,
        [Text.UTF8Encoding]::new($false)
    )
    Write-Host "README_UPDATED=True"
}
else {
    Write-Host "README_UPDATED=False"
    Write-Host "README_UPDATE_AVAILABLE=Rerun with -UpdateReadme"
}

Write-Host "BMW13_OFFICIAL_ROAD_TEST=PASS"
Write-Host "BMW14_LIVE_FAULT_MATRIX=PASS"
Write-Host "BMW15_OFFLINE_REGRESSION=PASS"
Write-Host "BMW15_RELEASE_BUILD=PASS"
Write-Host "BMW15_DIFF_CHECK=PASS"
Write-Host "QA_JSON=$qaJsonPath"
Write-Host "QA_DOCUMENT=$qaDocumentPath"
Write-Host "BMW15_FINAL_QA=PASS"
Write-Host "NEXT=Review git status and staged diff before the final commit."
