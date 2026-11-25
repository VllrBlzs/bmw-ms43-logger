# BMW-14/15 - final fault matrix and repository closeout

BMW-13 and the live GUI are already accepted. They are not repeated in this
runbook. The only remaining vehicle operation is BMW-14 transport
failure/recovery with the engine off and ignition initially in position II.

## Files to install

Copy these files into the existing repository:

- `run-bmw14-live-fault-matrix.ps1` ->
  `scripts\run-bmw14-live-fault-matrix.ps1`
- `complete-bmw15-final-qa.ps1` ->
  `scripts\complete-bmw15-final-qa.ps1`
- this document -> `docs\BMW-14_15_FINAL_RUNBOOK.md`

## Before visiting the car

At the charger, run the existing offline gate. Do not repeat BMW-13 or the GUI
acceptance.

```powershell
# Run from the repository root.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\test-bmw14-offline.ps1"
```

Continue only after `BMW14_OFFLINE=PASS`.

## One stationary car visit

Required physical state:

- vehicle stationary and secured;
- engine remains off for the complete matrix;
- ignition initially in position II;
- INPA closed;
- K+DCAN available as COM5.

Run:

```powershell
# Run from the repository root.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw14-live-fault-matrix.ps1" `
  -ConfirmReadOnly
```

The script pauses before every physical action. Enter the exact word `READY`
only after the requested state is established. After `READY`, keep the cable connected and ignition in position II until `ACTION_NOW` tells you to unplug the cable or switch the ignition off.

The five phases are:

1. 15-second baseline capture, ignition II;
2. USB diagnostic cable loss;
3. cable reconnect and a new 15-second capture;
4. ignition-off transport loss;
5. ignition II recovery and a new 15-second capture.

Success requires:

- all five fresh phase reports pass;
- cable and ignition loss create transport faults requiring reconnect;
- all three normal captures complete without a fault;
- the interface is released after every phase;
- all five phases use distinct acquisition epoch IDs.

The final line must be:

```text
BMW14_LIVE_FAULT_MATRIX=PASS
```

## BMW-15 at the charger

No additional vehicle interaction is required. Run:

```powershell
# Run from the repository root.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\complete-bmw15-final-qa.ps1" `
  -UpdateReadme
```

The script locates the existing passing BMW-13 official road report and the new
BMW-14 matrix report, reruns the complete offline regression, performs a final
Release build and diff check, writes `docs\BMW-15_FINAL_QA.md`, and updates the
managed BMW-15 status section in `README.md`.

Continue to Git only after `BMW15_FINAL_QA=PASS`.

## Final Git review and commit

Do not use `git add .`. First inspect the repository:

```powershell
git status --short
git --no-pager diff -- README.md docs scripts src tools BmwMs43Logger.sln
```

Do not commit anything under `data\private`. Stage the intended source,
documentation and solution changes explicitly after reviewing the status.

Then run:

```powershell
git diff --cached --check
if ($LASTEXITCODE -ne 0) {
    throw "Staged diff check failed."
}

git --no-pager diff --cached --stat
git commit -m "BMW-15 finalize validated read-only MS43 logger"
git status --short
git log -1 --oneline
```

The raw live CSV files and private acceptance directories are evidence inputs,
not repository content. The committed QA document contains bounded metrics and
hashes without publishing the raw vehicle data.
