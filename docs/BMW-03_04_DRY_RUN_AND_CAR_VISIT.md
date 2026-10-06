# BMW-03/04 – dry run and next bounded car visit

## What is ready

- 230/230 MS43 jobs have an offline review state.
- 1 job is live validated: `STATUS_MOTORDREHZAHL`.
- 85 zero-argument STATUS jobs are conditional candidates.
- 144 jobs are excluded from the v1 live-data surface.
- The next car test is restricted to 13 exact tuples in the pinned test pack.
- The BMW-04 draft contains 28 core parameters: 1 approved and 27 pending live validation.

The 253 raw numeric result candidates are not 253 distinct sensors. They contain aliases,
duplicated single-value accessors and values already present in the batch job.

## Copy map

Copy the supplied files to the BMW repository without renaming their contents:

| Supplied file | Destination under `C:\Users\Valler\Desktop\Biznisz\BMW` |
| --- | --- |
| `BMW-03_READ_ONLY_JOB_REVIEW.md` | `docs\BMW-03_READ_ONLY_JOB_REVIEW.md` |
| `bmw03-reviewed-job-inventory.json` | `config\ms430ds0-reviewed-job-inventory.json` |
| `bmw03-reviewed-job-inventory.csv` | `config\ms430ds0-reviewed-job-inventory.csv` |
| `ms430ds0-bounded-live-test-pack.json` | `config\ms430ds0-bounded-live-test-pack.json` |
| `ms430ds0-parameter-catalog.draft.json` | `config\ms430ds0-parameter-catalog.draft.json` |
| `bmw04-core-parameter-catalog.draft.csv` | `config\ms430ds0-parameter-catalog.draft.csv` |
| `bmw04-parameter-catalog.raw.csv` | `config\ms430ds0-parameter-catalog.raw.csv` |
| `bmw04-catalog-coverage.json` | `config\ms430ds0-catalog-coverage.json` |
| `test-bmw04-catalog.ps1` | `scripts\test-bmw04-catalog.ps1` |
| `BmwMs43Logger.BoundedLiveProbe.csproj` | `tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj` |
| `BoundedLiveProbe.Program.cs` | `tools\BmwMs43Logger.BoundedLiveProbe\Program.cs` |

## At home, on charger

Run from the repository root:

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

dotnet sln ".\BmwMs43Logger.sln" add `
  ".\tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj"

$ediabasProject = ".\third_party\ediabaslib\EdiabasLib\EdiabasLib\EdiabasLib.csproj"
if ((dotnet sln ".\BmwMs43Logger.sln" list) -match "EdiabasLib\\EdiabasLib\\EdiabasLib.csproj") {
    dotnet sln ".\BmwMs43Logger.sln" remove $ediabasProject
}

dotnet build `
  ".\tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj" `
  -c Release
if ($LASTEXITCODE -ne 0) { throw "Bounded probe build failed." }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\test-bmw04-catalog.ps1"
if ($LASTEXITCODE -ne 0) { throw "Catalog preflight failed." }

dotnet run `
  --project ".\tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj" `
  -c Release --no-build -- `
  --preflight
if ($LASTEXITCODE -ne 0) { throw "Probe preflight failed." }
```

Expected final lines: `COMMUNICATION_ATTEMPTED=False`, `INTERFACE_INSTANCE=None`,
`RESULT=PASS`.

Exercise every group without creating an interface or touching the COM port:

```powershell
$project = ".\tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj"
foreach ($group in @("baseline", "core", "states", "extended")) {
    dotnet run --project $project -c Release --no-build -- `
      --dry-run --group $group --passes 3
    if ($LASTEXITCODE -ne 0) { throw "Dry run failed: $group" }
}
```

Each dry run must say `COMMUNICATION_ATTEMPTED=False` and `RESULT=PASS`. Synthetic
reports go to `data\private\bmw03-live`; they are not car evidence.

## At the car

Before starting: close INPA/Tool32 and every other diagnostic application, connect the
known FTDI cable on COM5, and use the already-built Release binary. Stop the sequence on
the first `RESULT=FAIL`.

### Ignition on, engine off

Keep this phase short:

```powershell
$project = ".\tools\BmwMs43Logger.BoundedLiveProbe\BmwMs43Logger.BoundedLiveProbe.csproj"

dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario engine-off --group baseline --passes 1

dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario engine-off --group core --passes 1
```

### Engine idling

After idle has settled, run:

```powershell
dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario idle --group baseline --passes 1

dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario idle --group core --passes 3

dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario idle --group states --passes 1

dotnet run --project $project -c Release --no-build -- `
  --live --confirm-read-only --scenario idle --group extended --passes 1
```

The four groups contain 1, 1, 1 and 10 immutable jobs. Every argument is the exact empty
string. No command accepts a job name, result name or raw payload from the command line.

## Output to bring back

The real reports are written to:

`C:\Users\Valler\Desktop\Biznisz\BMW\data\private\bmw03-live`

Bring back only the six `bmw03_engine-off_*.json` and `bmw03_idle_*.json` files from the
sequence above, plus the bounded console summaries. Do not upload the synthetic dry-run
reports as live evidence.

At the end, opening INPA once is an optional independent port-release check: it proves
another application can acquire COM5 after the probe disposed its interface. It is not an
ECU write test and it is not required between the six commands.
