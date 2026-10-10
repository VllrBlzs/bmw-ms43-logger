# BMW-14 – timeout, disconnect, ignition-off and reconnect

## Outcome

BMW-14 replaces the GUI's direct dependency on the simulator with the
`ILoggerRuntime` boundary. The unchanged GUI/CSV/live-value/plot pipeline can
now receive either the offline simulator or the pinned EDIABAS MS43 backend.

The application still starts in offline simulation unless `--live` and
`--confirm-read-only` are both present. Constructing or opening the offline GUI
does not create an EDIABAS interface.

## Live safety boundary

The live runtime accepts only the pinned `ms430ds0.prg`, pinned EdiabasLib DLL,
pinned 28-entry catalog and pinned 13-job review pack. The selectable runtime
surface is the 22 parameters approved by BMW-04B.

Every ECU dispatch is checked immediately before execution:

- job name starts with `STATUS_`;
- argument is the exact empty string;
- result list is the exact allowlisted list;
- execution uses one serial worker;
- an unknown parameter, job, argument or result tuple is rejected;
- no write, clear, reset, actuator or control job is exposed by the runtime.

`Connect` performs one read-only `STATUS_MOTORDREHZAHL` handshake. `Start`
creates a new acquisition epoch and a new CSV/metadata pair.

## Failure policy

| Failure | Offline proof | Required live proof | Runtime decision |
| --- | --- | --- | --- |
| EDIABAS timeout | deterministic policy and faulted metadata | ignition-off or transport timeout | one retry, then stop |
| USB/cable removal | deterministic transport policy | unplug USB diagnostic cable, engine off | fault, flush, release interface |
| COM5/port loss | deterministic transport policy | covered by cable removal | fault, flush, release interface |
| ignition off | deterministic timeout policy | key off while engine remains off | fault, flush, release interface |
| file I/O failure | injected storage failure | no destructive live injection | stop; fault metadata when storage permits |
| reconnect | state-machine and epoch tests | reconnect cable/key and run short captures | new session and epoch |

The transport attempt count is capped at two: the original read plus one retry.
Storage faults are never retried as acquisition. A transport fault invalidates
the EDIABAS instance; the GUI permits only Disconnect, followed by a new
Connect. The next Start resets the live buffers and creates a new epoch, so a
previous value cannot become a sample in the recovered session.

## Offline command

Run from the BMW repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\test-bmw14-offline.ps1"
```

Expected final lines:

```text
BMW14_REPORT_ROUNDTRIP=PASS
BMW14_OFFLINE=PASS
BMW14_LIVE_CAR_VALIDATION_REQUIRED=True
```

The offline result does not close BMW-14. The cable, ignition-off and recovery
phases in the final car visit must also pass.

## GUI startup modes

Offline (default and communication-incapable):

```powershell
dotnet run --project ".\src\BmwMs43Logger.App\BmwMs43Logger.App.csproj" `
  -c Release --no-build
```

Live, explicitly armed:

```powershell
dotnet run --project ".\src\BmwMs43Logger.App\BmwMs43Logger.App.csproj" `
  -c Release --no-build -- `
  --live --confirm-read-only --repo-root "$PWD" `
  --ecu-path "C:\EDIABAS\ECU" --port COM5
```

The live banner is red and names COM5 plus the pinned MS43 profile. INPA or any
other program using the interface must be closed while this application runs.

## Repository files

- `src/BmwMs43Logger.Core/LoggerRuntime.cs`
- `src/BmwMs43Logger.Core/SimulatedRuntimeAdapter.cs`
- `src/BmwMs43Logger.Core/SimulatedLoggerRuntime.cs`
- `src/BmwMs43Logger.Ediabas/Ms43LiveProfile.cs`
- `src/BmwMs43Logger.Ediabas/EdiabasReadOnlyClient.cs`
- `src/BmwMs43Logger.Ediabas/EdiabasLoggerRuntime.cs`
- `src/BmwMs43Logger.Ediabas/BmwMs43Logger.Ediabas.csproj`
- `src/BmwMs43Logger.App/AppRuntimeOptions.cs`
- `src/BmwMs43Logger.App/MainForm.cs`
- `src/BmwMs43Logger.App/Program.cs`
- `src/BmwMs43Logger.App/BmwMs43Logger.App.csproj`
- `tools/BmwMs43Logger.RecoveryProbe/*`
- `tools/BmwMs43Logger.LiveAcceptanceProbe/*`
- `tools/BmwMs43Logger.RoadTestAnalyzer/*`
- `scripts/test-bmw14-offline.ps1`
- `scripts/run-bmw13-14-final-car-visit.ps1`
