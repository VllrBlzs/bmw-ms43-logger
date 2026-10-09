# BMW-09 – offline GUI state-machine foundation

## Scope of this increment

This increment defines and verifies the control state machine that the Windows GUI will use. It deliberately performs no ECU communication and creates no interface instance.

The guarded path is:

`Disconnected → Connecting → Ready → Starting → Logging → Stopping → Ready → Disconnecting → Disconnected`

Failures enter `Faulted`. From there the backend can be disconnected safely or explicitly reset. Every asynchronous operation receives an immutable operation ID; stale or duplicate completions are rejected.

## Why it is separate from the window

The button layout is not the risky part. The failure modes are duplicate pollers, duplicate CSV writers, stale asynchronous callbacks and disconnect while logging. These rules belong in a tested core state machine rather than event-handler conditionals scattered through a form.

## Copy map

| Supplied file | Destination in the BMW repository |
| --- | --- |
| `LoggerUiState.cs` | `src\BmwMs43Logger.Core\LoggerUiState.cs` |
| `BmwMs43Logger.GuiStateProbe.csproj` | `tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj` |
| `Program.cs` | `tools\BmwMs43Logger.GuiStateProbe\Program.cs` |
| `BMW-09_GUI_STATE_MACHINE.md` | `docs\BMW-09_GUI_STATE_MACHINE.md` |

## Offline verification

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

dotnet sln ".\BmwMs43Logger.sln" add `
  ".\tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj"

dotnet build `
  ".\tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj" `
  -c Release
if ($LASTEXITCODE -ne 0) { throw "BMW-09 state-machine build failed." }

dotnet run `
  --project ".\tools\BmwMs43Logger.GuiStateProbe\BmwMs43Logger.GuiStateProbe.csproj" `
  -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "BMW-09 state-machine probe failed." }
```

Expected final lines include:

```text
COMMUNICATION_ATTEMPTED=False
DUPLICATE_START_GUARD=PASS
STALE_COMPLETION_GUARD=PASS
CONCURRENT_CONNECT_GUARD=PASS
RESULT=PASS
```

This is the offline foundation of BMW-09, not the completed GUI. The next increment binds this state machine to the Windows form and a simulated logger runtime; the live EDIABAS adapter is attached only after the same GUI flow passes offline.
