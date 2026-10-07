# BMW-07 – CSV logger és session metadata

## Állapot

Ez a checkpoint a BMW-06 `NormalizedCycle` / `NormalizedSample` kimenetét írja
visszaolvasható fájlokba. Az offline dry run nem nyit COM portot, nem példányosít
EDIABAS interfészt és nem kommunikál ECU-val.

Az offline PASS önmagában nem zárja le a valós autós elfogadást. A BMW-08 során
ugyanez a logger legalább 15 perces állóhelyi mérésen kap valódi mintákat.

## Kimenetek

Minden logging session két összetartozó fájlt hoz létre:

- `<stem>.csv`: hosszú formátumú mintatáblázat;
- `<stem>.meta.json`: session-, verzió-, ütem- és hibaszámlálók.

Egy reconnect új acquisition epochot és új logging sessiont igényel. Külön
epochok nem írhatók ugyanabba a fájlba.

## CSV-séma

Sémaverzió: `bmw07.long_csv.v1`

Egy sor egy kiválasztott paraméter egy ciklusbeli állapotát jelenti. Hiányzó,
timeoutos vagy hibás eredményhez is készül sor; ezek értékmezője üres, nem `0`.

| Oszlop | Jelentés |
| --- | --- |
| `schema_version` | A CSV-séma verziója. |
| `session_id` | Nem személyes, fájlon belül állandó session-azonosító. |
| `epoch_id` | Acquisition epoch UUID; reconnectkor változik. |
| `cycle_id` | Szigorúan növekvő ciklusazonosító. |
| `cycle_completed_utc` | A teljes ciklus lezárási ideje UTC-ben. |
| `cycle_completed_monotonic_ticks` | A ciklus lezárása monotonic órán. |
| `parameter_id` | Stabil belső paraméter-ID. |
| `job_sequence` | A cikluson belüli soros job sorszáma. |
| `source_job` | Az allowlistes EDIABAS source job neve. |
| `job_started_utc` | A source job kezdete UTC-ben. |
| `job_finished_utc` | A source job vége UTC-ben. |
| `acquired_utc` | A minta acquisition ideje; hiánynál üres. |
| `monotonic_ticks` | A minta monotonic ideje; hiánynál üres. |
| `value` | Invariant-kultúrájú érték; érvénytelen mintánál üres. |
| `value_type` | `integer`, `real`, `boolean`, `string`, `datetime` vagy `json`. |
| `unit` | Forrásból ellenőrzött mértékegység; ismeretlennél üres. |
| `quality` | `valid`, `missing`, `timeout`, `stale` vagy `error`. |
| `error_code` | Géppel feldolgozható hibakód; normál mintánál üres. |
| `acquisition_ms` | A source job teljes időablaka. |
| `latency_ms` | Jobkezdet és mintavétel közötti idő. |
| `age_at_cycle_end_ms` | A minta életkora a ciklus lezárásakor. |

A CSV UTF-8, BOM nélküli, vesszővel tagolt és RFC 4180 szerinti idézőjeles
escapinget használ. A számok kultúrafüggetlen ponttal kerülnek kiírásra.

## Metadata-séma

Sémaverzió: `bmw07.session_metadata.v1`

A metadata tartalmazza:

- a CSV- és metadata-sémaverziót;
- a session státuszát, stop okát és opcionális fault kódját;
- UTC- és monotonic kezdő/záró időt;
- a kért és a ciklusokból számított tényleges Hz-et;
- az ECU-profil, SGBD, backend és katalógus verzióját/hashét;
- a kiválasztott paraméter-ID-ket;
- a CSV és metadata fájlnevét, abszolút útvonal nélkül;
- a lezárt ciklusok, kiírt minták és quality állapotok számlálóit;
- az esetleges félbemaradt ciklus ID-jét;
- a bounded queue drop- és fájlírási hibaszámlálóit;
- a lezárt CSV SHA-256 hashét.

## Lezárási szabályok

- Normál stop: a CSV flush és lezárás után hash készül, majd atomikusan létrejön
  a `completed` metadata.
- Acquisition hiba: a már kiírt CSV lezárható és `faulted` metadata készül.
- CSV-írási hiba: a logger megőrzi a sikeresen kiírt teljes sorokat, jelöli a
  félbemaradt ciklust, növeli a `write_errors` számlálót, majd megkísérli a
  `faulted/storage_error` metadata létrehozását.
- Ha a háttértár teljesen elérhetetlen, sem a CSV, sem a metadata tartós
  létrehozása nem garantálható; ezt a GUI-nak külön látható hibaként kell
  továbbadnia.
- `Dispose` explicit stop nélkül faulted sessiont eredményez; nem tekinthető
  sikeres mérésnek.

## Adatvédelmi határ

A metadata csak profil-ID-ket, fájlneveket és hasheket fogad. Nem tárol VIN-t,
abszolút felhasználói útvonalat vagy harmadik fél PRG/GRP tartalmát. Saját log
publikálása előtt az értékoszlopokat és a session környezetét ettől függetlenül
anonimizálni kell.

## Offline ellenőrzés

```powershell
dotnet run `
  --project ".\tools\BmwMs43Logger.CsvLoggerProbe\BmwMs43Logger.CsvLoggerProbe.csproj" `
  -c Release --no-build -- `
  --dry-run --output ".\artifacts\bmw07"
```

A probe ellenőrzi a normál start/stopot, a CSV és JSON round-tripet, az
idézőjeles értékeket, a quality számlálókat, a queue drop láthatóságát, egy
injektált részleges fájlírási hibát, a CSV hashét és az útvonal/VIN privacy
mezőket. `COMMUNICATION_ATTEMPTED=False` kötelező.
