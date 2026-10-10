# BMW-13/14 – one final car-visit runbook

## What is and is not combined

BMW-14 fault/recovery and BMW-13 road validation can happen during the same
physical visit. They remain separate CSV sessions and separate acquisition
epochs. The fault tests run with the engine **off** and ignition on. The road
test is one later, clean 15-minute GUI session.

Do not run the road phase until the vehicle is mechanically safe for normal
road use. The driver must not touch the laptop. A passenger operates it.

## Before leaving the charger

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw13-14-final-car-visit.ps1" `
  -Phase Offline
```
Continue only after `BMW14_OFFLINE=PASS`. This phase performs no ECU
communication. Charge the laptop fully; the live sequence normally takes about
20–25 minutes plus setup time.

## One car visit – fault matrix, then road test

Close INPA. Secure the laptop and cable. Initially leave the engine off and turn
the ignition to position II.

```powershell
Set-Location "C:\Users\Valler\Desktop\Biznisz\BMW"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw13-14-final-car-visit.ps1" `
  -Phase AllLive `
  -ConfirmReadOnly `
  -ConfirmVehicleSafe `
  -ConfirmPassengerOperator
```

The script pauses before every physical action and accepts only the exact word
`READY`.

### BMW-14 phases

1. **Baseline** – ignition on, engine off, 15-second normal capture.
2. **Cable loss** – after `ACTION_NOW`, unplug only the USB diagnostic cable.
3. **Cable recovery** – reconnect, wait for COM5, 15-second new-epoch capture.
4. **Ignition loss** – engine still off; after `ACTION_NOW`, turn ignition fully
   off.
5. **Ignition recovery** – turn ignition back to position II and run another
   15-second new-epoch capture.

All five sessions must have distinct epoch IDs. Both loss sessions must be
faulted, flushed and marked as requiring reconnect. The three normal sessions
must complete normally. Each phase disposes the interface before the next one.

### BMW-13 phase

The script then opens the live GUI. In the GUI:

1. click **Select approved**; the selection counter must show 22;
2. keep between one and five Plot boxes checked;
3. click **Connect**, then **Start logging** while safely stopped;
4. the passenger holds and operates the laptop; the driver does not interact;
5. drive normally and legally for at least 15 minutes, with ordinary idle,
   part-load and modest acceleration only;
6. after at least 15 minutes, safely stopped, click **Stop**;
7. click **Disconnect**, then close the GUI.

The script automatically selects the newly created run and applies the BMW-13
acceptance rules:

- exactly one new epoch;
- exactly 22 BMW-04B-approved parameters;
- duration at least 840 seconds (14-minute tolerance around the 15-minute
  target);
- effective cadence at least 1.5 Hz;
- at least 98% valid rows;
- zero queue drops and zero write errors;
- CSV SHA-256 and metadata round-trip match;
- vehicle speed reaches at least 5 km/h and RPM span reaches at least 300 rpm;
- no VIN, absolute path or licensed descriptor in metadata.

The final success line is `RESULT=PASS`. The authoritative report is
`bmw13-road-test-acceptance.json` in that road-test run directory.

## Resume commands if the visit is split

Fault matrix only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw13-14-final-car-visit.ps1" `
  -Phase Faults -ConfirmReadOnly
```

Road test only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw13-14-final-car-visit.ps1" `
  -Phase RoadTest -ConfirmReadOnly `
  -ConfirmVehicleSafe -ConfirmPassengerOperator
```

Re-analyze an existing road run without communicating with the car:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ".\scripts\run-bmw13-14-final-car-visit.ps1" `
  -Phase Analyze `
  -RunDirectory "C:\path\to\bmw14_live_..."
```
