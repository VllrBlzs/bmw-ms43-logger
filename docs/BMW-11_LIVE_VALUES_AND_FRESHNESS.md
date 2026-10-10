# BMW-11 – live values, unit and freshness

## Outcome

BMW-11 turns the BMW-10 parameter table into an explicit live-value view. Each
selected row now shows:

- formatted value;
- unit;
- UTC acquisition timestamp;
- monotonic sample age;
- `valid`, `stale`, `missing`, `timeout` or `error` quality.

This increment remains an offline simulation. It creates no EDIABAS interface,
does not open a COM port and cannot dispatch an ECU job. Its purpose is to prove
the display and thread boundary before the already-reviewed read-only acquisition
source is attached.

## Freshness policy

The displayed age is calculated from `Stopwatch` monotonic ticks, not from the
wall clock. A valid value becomes stale when its age exceeds the greater of:

- 1500 ms; or
- three effective sampling periods.

The last known value may remain visible after it becomes stale, but its quality
changes to `stale`, its colour changes and `AGE_LIMIT_EXCEEDED` is shown. Missing,
timeout and error observations never display a fabricated zero.

The timestamp column is the sample's acquisition time in UTC. It is not the GUI
redraw time and not the CSV write time.

## Polling/UI separation

The acquisition worker publishes normalized cycles into a thread-safe
latest-frame buffer. The GUI reads the newest frame every 250 ms. Reading the
buffer does not call the runtime, enqueue a job or alter the acquisition cycle
count. If the GUI is temporarily slow, intermediate display frames may be skipped,
while CSV logging continues from every normalized acquisition cycle.

## Value formatting

Numeric precision is unit-aware. RPM is shown without artificial decimals;
temperatures, angles, speed and percentages use one decimal; voltage uses two;
injection time uses three. Boolean values are rendered as `ON`/`OFF`, and string
values are trimmed rather than coerced to a number. The current 22 approved MS43
mappings are numeric, but non-numeric formatting is covered by the offline probe.

## Files

| Source artifact | Repository destination |
| --- | --- |
| `BMW11.LiveValueDisplay.cs` | `src/BmwMs43Logger.Core/LiveValueDisplay.cs` |
| `BMW11.SimulatedLoggerRuntime.cs` | `src/BmwMs43Logger.Core/SimulatedLoggerRuntime.cs` |
| `BMW11.MainForm.cs` | `src/BmwMs43Logger.App/MainForm.cs` |
| `BMW11.LiveValueProbe.csproj` | `tools/BmwMs43Logger.LiveValueProbe/BmwMs43Logger.LiveValueProbe.csproj` |
| `BMW11.LiveValueProbe.Program.cs` | `tools/BmwMs43Logger.LiveValueProbe/Program.cs` |
| `BMW-11_LIVE_VALUES_AND_FRESHNESS.md` | `docs/BMW-11_LIVE_VALUES_AND_FRESHNESS.md` |

## Offline probe

```powershell
dotnet run `
  --project ".\tools\BmwMs43Logger.LiveValueProbe\BmwMs43Logger.LiveValueProbe.csproj" `
  -c Release
```

Expected terminal decision:

```text
BMW-11 OFFLINE LIVE VALUE DISPLAY DRY RUN
COMMUNICATION_ATTEMPTED=False
INTERFACE_INSTANCE=None
VALID_FORMATTING=PASS
UNIT_AND_TIMESTAMP=PASS
STALE_TRANSITION=PASS
MISSING_TIMEOUT_ERROR=PASS
NON_NUMERIC_FORMATTING=PASS
LATEST_FRAME_BUFFER=PASS
GUI_READ_DOES_NOT_POLL=PASS
SIMULATED_RUNTIME_ROUNDTRIP=PASS
RESULT=PASS
```

The machine-readable report is written to:

```text
artifacts\bmw11\bmw11-live-value-display-dryrun.json
```

## GUI smoke check

1. Start the application without any live/ECU switch.
2. Confirm the orange banner contains `COMMUNICATION_ATTEMPTED=False`.
3. Keep the default four parameters, then press `Connect` and `Start logging`.
4. Confirm value, unit, `Acquired at`, `Age` and `Quality` update.
5. Confirm `Acquired at` changes only when a new simulated acquisition cycle is
   published, while `Age` continues to change on GUI redraws.
6. Stop the run. After the freshness limit expires, the retained values must turn
   orange and become `stale: AGE_LIMIT_EXCEEDED`.
7. Start a second run. Values must reset to `missing` before the new epoch's first
   sample arrives; values from the previous run must not appear as fresh.
8. Stop, disconnect and close the application.

## Remaining gate

BMW-11 offline acceptance is not proof of in-car display. The next hardware gate
must feed this same buffer from the pinned, reviewed MS43 read-only acquisition
path. No arbitrary job, argument, result name, clear/reset operation or active test
may be exposed through the GUI.
