# BMW-10 – searchable parameter manager and immutable run selection

## Outcome

BMW-10 adds an offline-testable parameter manager to the BMW MS43 logger GUI.
It does not create an ECU interface and it does not add a general EDIABAS job API.

The manager exposes all 28 reviewed catalog mappings:

- 22 mappings have sufficient BMW-04B live-observation evidence and are selectable;
- 6 mappings remain visible but cannot be selected;
- every run captures one immutable selection before logging starts;
- the captured selection is written to session metadata and defines the CSV rows;
- changing the selection while a session is active is rejected.

## Withheld mappings

| Parameter ID | Status |
| --- | --- |
| `coolant.outlet.temperature` | `withheld_semantic_validation` |
| `ignition.cylinder1.advance` | `withheld_semantic_validation` |
| `idle.integrator` | `withheld_semantic_validation` |
| `vanos.intake.actual.angle` | `withheld_dynamic_validation` |
| `vanos.exhaust.actual.angle` | `withheld_dynamic_validation` |
| `cooling.fan.ac.request.stage` | `withheld_naming_validation` |

These entries stay in the searchable catalog so that the evidence gap is visible.
They are not silently deleted and they are not exposed to session dispatch.

## Search and selection rules

The search field matches:

- Hungarian display name;
- stable parameter ID;
- internal name;
- unit;
- support status.

The default selection contains:

- `engine.rpm`;
- `vehicle.speed`;
- `engine.coolant.temperature`;
- `electrical.battery.voltage`.

At least one approved parameter is required before `Start logging` is enabled.
`Select approved` selects all 22 approved mappings. `Clear selection` creates an
empty draft selection and therefore blocks Start.

## Run-schema boundary

Pressing Start captures and locks the current selection. Search remains harmless,
but checkbox changes and bulk-selection commands are blocked until Stop completes.
The simulated runtime accepts only the captured approved IDs and emits only those
IDs to `session.csv`. `selected_parameter_ids` in `session.meta.json` must match the
CSV parameter set exactly.

A changed selection starts a new directory and a new metadata document. Existing
CSV files are never widened or narrowed in place.

## Sampling-budget display

The GUI displays the BMW-05 simulated virtual cost model. It is a planning estimate,
not a new real-car benchmark:

| Selection shape | Jobs | Exact result requests | Virtual median cycle | Estimated ceiling |
| --- | ---: | ---: | ---: | ---: |
| RPM only | 1 | 4 | 191.00 ms | 5.24 Hz |
| Any batch-only selection | 1 | 57 | 219.25 ms | 4.56 Hz |
| RPM plus any batch parameter | 2 | 61 | 410.25 ms | 2.44 Hz |

The batch request count stays 57 because the reviewed read-only tuple is exact and
is not narrowed dynamically. Selecting fewer displayed batch parameters reduces the
CSV schema, not the currently modelled EDIABAS result tuple. A later live benchmark
may replace this estimate; BMW-10 does not claim that the car will sustain the
requested GUI rate.

## Offline probe

```powershell
dotnet run `
  --project ".\tools\BmwMs43Logger.ParameterManagerProbe\BmwMs43Logger.ParameterManagerProbe.csproj" `
  -c Release
```

The probe checks catalog counts, search, withheld/unknown/duplicate rejection,
active-session locking, the three budget shapes, and two independent simulated runs
with different immutable CSV schemas.

Expected terminal decision:

```text
CATALOG_ENTRIES=28
SELECTABLE=22
WITHHELD=6
SEARCH_BY_DISPLAY_INTERNAL_ID=PASS
WITHHELD_SELECTION_GUARD=PASS
ACTIVE_SESSION_SELECTION_LOCK=PASS
SAMPLING_BUDGET_MODEL=PASS
CSV_SCHEMA_ISOLATION=PASS
RESULT=PASS
```

## GUI smoke check

1. Launch the app without any live switch.
2. Search for `VehicleSpeed`, `hűtőfolyadék`, and `withheld`.
3. Confirm that the six withheld rows cannot be checked.
4. Select four approved parameters and press Connect, then Start.
5. Confirm that selection controls are locked during the run.
6. Stop and confirm `samples = cycles × selected parameters`.
7. Change the selection, start a second run, and confirm a separate output folder.

All BMW-10 acceptance work is offline. `COMMUNICATION_ATTEMPTED=False` remains a
hard, visible statement.
