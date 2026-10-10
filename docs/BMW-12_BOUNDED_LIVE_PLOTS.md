# BMW-12 – bounded live plots

## Outcome

BMW-12 adds an offline-testable live graph to the BMW MS43 logger GUI. The user
can mark between one and five of the logged numeric parameters for plotting.
Each channel is drawn in its own lane with its own value scale and unit, so RPM,
temperature, voltage and speed are not misleadingly forced onto one shared axis.

This increment remains an offline simulation. It does not instantiate EDIABAS,
open COM5 or dispatch an ECU job.

## Data-source rule

The CSV logger, live-value table and graph all consume the same
`NormalizedCycle` source. Acquisition pushes every cycle into a bounded plot
history. A 250 ms GUI timer only reads immutable snapshots and redraws the
control; it cannot request an additional acquisition cycle.

The graph x-axis uses each valid sample's `AcquiredAtUtc` value. A missing result
has no invented acquisition timestamp and is represented as a gap marker at the
cycle boundary.

The GUI records rendered-frame count plus last/maximum paint time in milliseconds
in its periodic progress log. The probe also reports mean immutable-snapshot read
time, making display overhead observable without applying a machine-specific
pass/fail timing threshold.

## Bounds

- maximum plot channels: 5;
- history window: 120 seconds;
- maximum retained points per series: 600;
- older points are dropped in memory and are not re-read from CSV;
- every lane uses a separately calculated y-range and displays its unit.

The plot is a live inspection view, not the authoritative archive. The CSV remains
the complete session output within the logger's normal storage policy.

## Quality and gaps

Only finite `valid` numeric samples are joined by a line. These states break the
line and receive a visible marker:

- `missing`;
- `timeout`;
- `stale`;
- `error`;
- non-numeric or non-finite values passed to a numeric channel.

The renderer never joins the valid points on opposite sides of a failed sample.
Historical samples that were valid at acquisition remain valid history after the
session stops; the BMW-11 table separately shows current age-derived staleness.

## Files

| Source artifact | Repository destination |
| --- | --- |
| `BMW12.LivePlotHistory.cs` | `src/BmwMs43Logger.Core/LivePlotHistory.cs` |
| `BMW12.LivePlotControl.cs` | `src/BmwMs43Logger.App/LivePlotControl.cs` |
| `BMW12.MainForm.cs` | `src/BmwMs43Logger.App/MainForm.cs` |
| `BMW12.LivePlotProbe.csproj` | `tools/BmwMs43Logger.LivePlotProbe/BmwMs43Logger.LivePlotProbe.csproj` |
| `BMW12.LivePlotProbe.Program.cs` | `tools/BmwMs43Logger.LivePlotProbe/Program.cs` |
| `BMW-12_BOUNDED_LIVE_PLOTS.md` | `docs/BMW-12_BOUNDED_LIVE_PLOTS.md` |

## Offline probe

```powershell
dotnet run `
  --project ".\tools\BmwMs43Logger.LivePlotProbe\BmwMs43Logger.LivePlotProbe.csproj" `
  -c Release
```

Expected decision:

```text
BMW-12 OFFLINE BOUNDED LIVE PLOT DRY RUN
COMMUNICATION_ATTEMPTED=False
INTERFACE_INSTANCE=None
CHANNEL_LIMIT=PASS
BOUNDED_HISTORY=PASS
ACQUISITION_TIME_AXIS=PASS
QUALITY_GAPS=PASS
NON_NUMERIC_GAP=PASS
GUI_REDRAW_DOES_NOT_POLL=PASS
CSV_SOURCE_ALIGNMENT=PASS
MAX_CHANNELS=5
MAX_POINTS_PER_SERIES=600
SNAPSHOT_READ_MEAN_US=<measured value>
RESULT=PASS
```

The machine-readable report is written to:

```text
artifacts\bmw12\bmw12-bounded-live-plot-dryrun.json
```

## GUI validation

1. Start the application without any live/ECU switch.
2. Confirm the banner contains `COMMUNICATION_ATTEMPTED=False`.
3. Keep the four default `Use` and `Plot` selections.
4. Connect and start logging.
5. Confirm four separate lanes appear and update without blocking buttons.
6. Confirm each lane shows the correct parameter name and unit.
7. Wait for a `render last/max=... ms` progress entry and confirm the counters
   advance while the acquisition cycle counter remains single-sourced.
8. Stop; the historical graph remains visible while the BMW-11 table becomes
   stale after its configured freshness limit.
9. Uncheck one `Plot` box and check another selected parameter. A sixth plot
   selection must be rejected.
10. Start a new run; the graph must clear before the new epoch and contain only
   the new plot selection.
11. Stop, disconnect and close the application.

## Remaining validation

Offline PASS proves bounds, time semantics, quality gaps and UI/acquisition
separation. It does not prove real-car rendering performance. The in-car gate must
later feed this unchanged normalized sample path from the pinned, allowlisted
read-only MS43 acquisition source.

## Validation evidence — 2026-10-10

Offline and manual GUI acceptance completed on Windows 11 x64:

- full Release solution build: PASS;
- offline bounded-history probe: PASS;
- communication attempted: false;
- maximum plot channels: 5;
- maximum retained points per series: 600;
- 700-cycle bounded-history test: PASS;
- 1000 GUI snapshot reads without additional polling: PASS;
- mean snapshot read time: 10.11 microseconds;
- five separately scaled live plot lanes: PASS;
- acquisition UTC axis and units: PASS;
- render timing visible; observed steady redraw approximately 3–4 ms,
  maximum observed 35.07 ms;
- valid-to-stale transition after Stop: PASS;
- sixth plot-channel rejection: PASS;
- new acquisition epoch clears previous plot history: PASS;
- BMW-12 simulated session prefix: PASS.

The screenshots remain private because they contain a local Windows user path.
Real ECU rendering and communication-failure validation remain outside BMW-12.
