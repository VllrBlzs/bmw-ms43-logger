# BMW-09 – minimal Windows GUI, offline increment

## Implemented

- WinForms window with `Connect`, `Start logging`, `Stop` and `Disconnect`.
- The GUI thread never executes the logging loop.
- The tested BMW-09 state machine controls button availability.
- A second start, stop or connect is rejected before a second runtime can be created.
- The output directory and the most recent session directory are visible.
- Stop finalizes `session.csv` and `session.meta.json` through the BMW-07 logger.
- Four representative live values are displayed from the same simulated cycles that feed the CSV.
- The simulation logs the 22 BMW-04B live-observed parameters. The six semantic-hold parameters are absent.
- No ECU interface, COM port, EDIABAS job or raw payload is available in this increment.

This is the correct offline acceptance stage. It is not evidence that the GUI has connected to the car.

## Copy map

| Supplied file | Destination in the BMW repository |
| --- | --- |
| `SimulatedLoggerRuntime.cs` | `src\BmwMs43Logger.Core\SimulatedLoggerRuntime.cs` |
| `BmwMs43Logger.App.csproj` | `src\BmwMs43Logger.App\BmwMs43Logger.App.csproj` |
| App `Program.cs` | `src\BmwMs43Logger.App\Program.cs` |
| `MainForm.cs` | `src\BmwMs43Logger.App\MainForm.cs` |
| Updated probe `Program.cs` | overwrite `tools\BmwMs43Logger.GuiStateProbe\Program.cs` |
| `BMW-09_OFFLINE_GUI.md` | `docs\BMW-09_OFFLINE_GUI.md` |

## Build and automated dry run

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

dotnet sln ".\BmwMs43Logger.sln" add `
  ".\src\BmwMs43Logger.App\BmwMs43Logger.App.csproj"

dotnet build ".\BmwMs43Logger.sln" -c Release
if ($LASTEXITCODE -ne 0) { throw "BMW-09 solution build failed." }

dotnet run `
  --project ".\tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj" `
  -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "BMW-09 offline runtime probe failed." }
```

The probe must report:

```text
COMMUNICATION_ATTEMPTED=False
SIMULATED_LOGGER_ROUNDTRIP=PASS
APPROVED_PARAMETER_COUNT=22
RESULT=PASS
```

## Manual GUI dry run

```powershell
dotnet run `
  --project ".\src\BmwMs43Logger.App\BmwMs43Logger.App.csproj" `
  -c Release --no-build
```

In the window:

1. Click `Connect`.
2. Click `Start logging`.
3. Let it run for 10–20 seconds.
4. Try clicking `Start logging` again: it must already be disabled.
5. Click `Stop`; wait for CSV finalization.
6. Click `Open last run` and confirm `session.csv` and `session.meta.json` exist.
7. Start and stop a second session. It must create a new directory, not append to the first session.
8. Click `Disconnect` and close the window.

The orange banner must continue to say `OFFLINE SIMULATION — COMMUNICATION_ATTEMPTED=False` throughout this test.

## Remaining BMW-09 work

The next increment replaces only the runtime adapter with the pinned, allowlisted EDIABAS acquisition path. The window and state machine remain the same. Live mode must not expose arbitrary job names, arguments or result names.
