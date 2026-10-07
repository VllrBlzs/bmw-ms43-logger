# BMW-08 – integrated stationary logging QA and car-visit runbook

## Scope

BMW-08 integrates the already committed BMW-04 catalog, BMW-05 polling planner,
BMW-06 sample-quality model and BMW-07 CSV logger. The live harness exposes no raw job,
result or argument input. It can execute only five pinned selections, and every resulting
dispatch tuple must match the hash-pinned, empty-argument read-only pack.

The remaining real-car gates are deliberately combined:

| Gate | Evidence produced |
| --- | --- |
| BMW-03/04 | The 13 reviewed jobs and the 28 catalog channels return real values/units. |
| BMW-05 | Median/p95 cycle time and effective Hz for five channel selections. |
| BMW-08 | Multiple start/stops, a 15-minute CSV, round-trip checks and disconnect handling. |

BMW-03, BMW-04, BMW-05 and BMW-08 remain open until their live output has been reviewed.
Passing an offline dry run is preparation, not car evidence.

## Files and one-time copy map

Copy the supplied files without changing their contents:

| Supplied file | Destination under `C:\Users\Valler\Desktop\Biznisz\BMW` |
| --- | --- |
| `BMW08.ControlledProfile.cs` | `tools\BmwMs43Logger.StationaryLogger\BMW08.ControlledProfile.cs` |
| `BMW08.CycleSources.cs` | `tools\BmwMs43Logger.StationaryLogger\BMW08.CycleSources.cs` |
| `BMW08.RunMetrics.cs` | `tools\BmwMs43Logger.StationaryLogger\BMW08.RunMetrics.cs` |
| `BMW08.StationaryLogger.Program.cs` | `tools\BmwMs43Logger.StationaryLogger\Program.cs` |
| `BmwMs43Logger.StationaryLogger.csproj` | `tools\BmwMs43Logger.StationaryLogger\BmwMs43Logger.StationaryLogger.csproj` |
| `run-bmw04-05-08-car-visit.ps1` | `scripts\run-bmw04-05-08-car-visit.ps1` |
| `BMW-08_CAR_VISIT_RUNBOOK.md` | `docs\BMW-08_CAR_VISIT_RUNBOOK.md` |

Do not create a ZIP. Create the destination directory if necessary, then copy these seven
individual files.

## At home on the charger

Run only this command from any PowerShell directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase Offline
```

This phase builds the new Release binary, runs both no-communication preflights, exercises
all five selections, injects a synthetic timeout, parses every generated CSV/JSON pair and
builds the full solution. It must end with:

```text
COMMUNICATION_ATTEMPTED=False
DRY_SELECTIONS=5
FAULT_HANDLING=PASS
RESULT=PASS
```

Do not go to the car until this phase is green. No build or source-file edit belongs in the
car visit.

## Before the live phases

1. Charge the laptop fully and disable sleep while plugged into the diagnostic session.
2. Connect the known FTDI cable and verify that Windows exposes exactly one healthy COM5.
3. Close INPA, Tool32, TestO and every other diagnostic application. Only one program may
   own COM5.
4. Keep the car stationary, in neutral/park, with the parking brake applied and adequate
   ventilation. Do not perform this procedure in a closed space.
5. Do not touch the accelerator unless a later, explicit test instruction calls for it.
   This visit requires only ignition-on/engine-off and stable idle.

For the BMW-04 comparison, first view a few representative stable values in INPA (RPM,
coolant temperature, battery voltage and throttle/accelerator angle), note their displayed
units or take a private screenshot, then close INPA completely. The logger and INPA must
never run concurrently. The comparison is approximate and sequential because the values
are not sampled at the same instant.

## Live phase order

Use the same script for each phase. Every live phase requires the explicit
`-ConfirmReadOnly` switch. Stop immediately on the first `RESULT=FAIL`.

### 1. Ignition on, engine off

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase EngineOff -ConfirmReadOnly
```

This runs two short bounded BMW-03/04 reads and a 20-second all-28 integrated log.

### 2. Stable idle catalog validation

Start the engine, let idle settle, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase IdleCatalog -ConfirmReadOnly
```

This executes all four bounded groups and a 30-second all-28 integrated log. It is the main
BMW-03/04 live catalog evidence.

### 3. BMW-05 real polling benchmark

Keep the engine at stable idle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase IdleBenchmark -ConfirmReadOnly
```

It records 20 seconds each for `rpm_only`, `core4`, `batch8`, `core16` and `all28`. Slow
larger selections are valid evidence; the logger does not start a parallel COM worker to
chase the requested rate.

### 4. BMW-08 start/stop and 15-minute acceptance

Keep the engine at stable idle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase StationaryAcceptance -ConfirmReadOnly
```

This creates two separate 30-second start/stop sessions and one separate 900-second,
all-28 session. Each session is finalized, hashed and parsed before the script continues.

### 5. Controlled disconnect proof

Run this last. Read the instruction printed by the script, then unplug only the USB side of
the diagnostic cable about 10 seconds after logging begins:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  "C:\Users\Valler\Desktop\Biznisz\BMW\scripts\run-bmw04-05-08-car-visit.ps1" `
  -Phase DisconnectProof -ConfirmReadOnly
```

Success here means the logger reports an acquisition fault, closes a parseable CSV and
metadata file, and does not continue emitting frozen values as fresh samples. Reconnect the
cable only after the program has stopped. A normal successful 60-second run is a failure of
this particular proof because no disconnect was observed.

## Time and battery budget

| Work | Expected time |
| --- | ---: |
| Setup, COM5 check and sequential INPA reference | 4–6 min |
| Engine-off and catalog phases | 2–3 min |
| Five polling benchmarks | about 2 min |
| Two start/stops plus 15-minute log | about 16 min |
| Disconnect, reconnect and output check | 2–4 min |
| **Expected total** | **26–31 min** |

This leaves roughly 9–14 minutes of reserve inside a 40-minute laptop-battery window. If
the remaining battery estimate drops below 10 minutes before `StationaryAcceptance`, stop
and continue that phase on the next visit; completed phase outputs remain valid.

## Output locations

Bounded BMW-03/04 reports:

`C:\Users\Valler\Desktop\Biznisz\BMW\data\private\bmw03-live`

Integrated BMW-04/05/08 CSV, metadata and acceptance reports:

`C:\Users\Valler\Desktop\Biznisz\BMW\data\private\bmw08-live`

Each integrated run has its own directory containing:

- `session.csv`
- `session.meta.json`
- `session.acceptance.json`

Bring back the console summaries, every `session.acceptance.json`, and every
`session.meta.json`. The acceptance JSON already contains observed units, value types,
first/last values and numeric ranges. Keep the full CSV files locally unless one specific
run needs row-level debugging; their SHA-256 hashes and round-trip result remain in the
small evidence files. Keep VIN-bearing INPA screenshots private; a written comparison of
the four representative values/units is enough.

## Read-only boundary

The live harness pins the exact SGBD, EdiabasLib, catalog and test-pack hashes. It accepts
only empty job arguments and only the reviewed result lists. It has no generic execute-job
command and rejects unknown selections. It does not expose coding, clearing, reset,
actuator, adaptation or memory-write operations. Physical communication can still fail or
be interrupted, so each command finalizes its local files and disposes the interface before
returning.
