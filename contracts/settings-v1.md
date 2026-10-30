# Settings v1

Capability `settings-v1`. App-wide settings, saved by the engine in its data folder. They apply to every product and build. The desktop never writes the file and never sends a path.

## Methods

- `settings.read`: empty parameters. Returns one `SettingsSnapshot`. Reading never writes the file.
- `settings.set`: `{name:"communityBuildHistory"|"fetchTableDefinitions"|"communityListfile", value:true|false}`. A change writes only that switch; every other saved value, a known switch's value this version doesn't understand, and unknown values are kept as written. Returns the committed `SettingsSnapshot`, read back from the file after the write. Request cancellation after the file is replaced doesn't withdraw the change. A failed change is never replayed automatically. The last change wins; there is no revision check.
- `settings.listfile.read` and `settings.listfile.check`: empty parameters, capability `community-listfile-v1` ([below](#community-listfile)). Each returns one `CommunityListfileStatus`.
- `settings.storage.measure`: empty parameters. One cancellable request that walks the data folder and returns a `StorageMeasurement`. One walk runs at a time; a second request waits for it. The walk stops at a 5-minute budget with `STORAGE_MEASURE_TIMEOUT`. It sends no progress, reads no file content and takes no download or CPU permits.

## SettingsSnapshot

- `formatVersion`: the settings format this engine writes, `1`.
- `file`: `absent` (no file yet, every value is its default), `read`, `unreadable` or `newer` (a later settings version). `problem` is set for `unreadable` and `newer` (`SETTINGS_VERSION`). An `unreadable` file is either read and found bad (`SETTINGS_UNREADABLE`: bad JSON or UTF-8, wrong shape, no version) or never read (`SETTINGS_NOT_READ`: larger than 64 KiB, changed while read, or not openable, such as a lock or a permission error; retryable except for the size).
- `lastChangedAt`: when the file was last written, with an explicit UTC offset, or null.
- `communityBuildHistory`: `value`, `defaultValue` (true, owner decision on #9, [D106](../docs/DECISIONS.md)) and `source`: `default` (no saved value), `saved`, or `unreadable` (the saved value, or the whole file, couldn't be used). An unreadable value carries `SETTING_UNREADABLE` or the file's problem. It's never used as if it were saved; an unreadable value reads as Off (`value: false`) for every switch. On: Refresh builds reads the wago.tools build list at most once a day and Discovery lists its builds. Off: community builds are hidden and wago.tools isn't contacted.
- `fetchTableDefinitions`: the same shape, `defaultValue` true. On: Tables and the feature views may fetch a newer definition from WoWDBDefs when no bundled one lists a build ([D081](../docs/DECISIONS.md), [D101](../docs/DECISIONS.md)). Off: bundled definitions and layout matching only. An `unreadable` value, or the whole file being `unreadable` or `newer`, reads as `value: false`: the engine fetches nothing until a value is saved. An older engine keeps this value as an unknown one.
- `communityListfile`: the same shape, `defaultValue` true. On: Files labels ROOT FileDataIDs with names from wow-listfile ([D105](../docs/DECISIONS.md)). Off: no names, and the engine doesn't contact GitHub for them. It fails closed exactly as `fetchTableDefinitions`. A change takes effect at once: off stops a running download and keeps its bytes; on starts or resumes it.
- `storage.dataDirectory`: the engine's absolute data folder, for display only.
- `downloadLimits`: the engine's HTTP permits (`parallelDownloads`, `backgroundDownloads`), `bandwidthCapBytesPerSecond` (null: no cap) and `adjustable` (false in this version).

## File

`settings-v1.json` in the data folder: `{"version":1,"changedAt":"…","values":{"communityBuildHistory":false,"fetchTableDefinitions":true,"communityListfile":true}}` (a switch never changed has no value), at most 64 KiB. A write goes to `settings-v1.json.tmp`, is flushed to disk and then replaces the file, so a failed write leaves the old file as it was (`SETTINGS_WRITE_FAILED`, retryable). Values the engine doesn't know, and top-level fields other than `version`, `changedAt` and `values`, are all kept as written on the next write, however many or deeply nested. A leading UTF-8 byte order mark is ignored. Text that doesn't decode anywhere in the file (invalid UTF-8, a lone surrogate escape) makes it `SETTINGS_UNREADABLE`. The 64 KiB bound is the only parse limit, so any positive integer version above 1, however large, is `newer`. A switch replaces only a file whose bytes were read and found bad (`SETTINGS_UNREADABLE`); the new file saves every other switch as it was shown, so fetching stays Off. A file that was never read is left alone: `settings.set` fails with `SETTINGS_NOT_READ`. A `newer` file is never overwritten: `settings.set` fails with `SETTINGS_VERSION`. The desktop shows the switch disabled with its reason in both cases.

## Community listfile

Capability `community-listfile-v1` ([D105](../docs/DECISIONS.md)). Schemas: `CommunityListfileStatus`, `CommunityListfileRelease`.

- `settings.listfile.read` reads the status. It never starts a download. The desktop reads it to show the download as one Downloads & activity task while `activity` isn't `idle`, or after a failure until it is dismissed or a later attempt succeeds.
- `settings.listfile.check` asks GitHub for the newest release now, skipping the wait after a failure, and starts the download when none has run yet. It returns at once, already `checking` when nothing else ran; the status shows the work. While the switch reads as off it fails with `LISTFILE_OFF`.

`CommunityListfileStatus`:

- `enabled`: the switch reads as on.
- `started`: something asked for names in this engine run, or a copy or partial download was kept from an earlier one. False with no `release` means nothing was ever downloaded: the first download starts when Files lists files, the switch is turned on, or `settings.listfile.check` asks.
- `activity`: `idle`, `verifying` (hashing the kept index at start), `checking` (asking GitHub), `downloading` or `importing`. `pendingTag`, `completed` and `total` (exact decimal byte strings: bytes received, or bytes of the CSV read while importing) are set only while downloading or importing.
- `release`: the verified copy names come from, or null: `tag`, `publishedAt` (GitHub's), `fetchedAt` (when this engine finished it), the CSV's `sha256` and `byteLength` as GitHub published them, `nameCount` and `sourceUrl`, always `https://github.com/wowdev/wow-listfile/releases/download/<tag>/community-listfile-withcapitals.csv`.
- `checkedAt`: when GitHub last confirmed the release in use.
- `problem`: the last attempt's failure, or null. A release in use stays in use. Codes: `LISTFILE_FETCH_OFFLINE` (GitHub not reachable, retryable), `LISTFILE_FETCH_FAILED` (an HTTP status, a limit, an interrupted download whose bytes are kept, a digest mismatch, a redirect elsewhere, storage; retryable), `LISTFILE_TOO_LARGE` (above 512 MiB), `LISTFILE_INVALID` (the parser refused the file; the same bytes aren't downloaded again) and `LISTFILE_NOT_FOUND` (the newest release has no such asset).

The engine reads `VIBEMINER_LISTFILE_ORIGIN` at start. Only a bare loopback `http` or `https` origin (`127.0.0.1`, `::1`, `localhost`, any port, no path) is used; anything else is ignored. With one, every listfile request keeps its path and goes to that origin instead of GitHub. It exists so test runs never reach GitHub; the e2e config sets it to `http://127.0.0.1:9/`.

## Prepared data per build

Capability `prepared-data-v1` ([D085](../docs/DECISIONS.md)). Schemas: `PreparedDataList`, `PreparedBuild`, `PreparedFileCatalog`, `PreparedTables`, `PreparedDownloads`, `PreparedDataFreeParams`, `PreparedDataFreed`.

- `settings.storage.builds`: empty parameters, cancellable. Every opened build, most recently opened first, at most 256 (`truncated` when more were opened), each measured when read, one catalog operation per build:
  - `fileCatalog`: its file catalog `state` (`none` when it was never queued, otherwise a [file catalog](inventory-v1.md#file-catalog-after-open) state, `removed` included) and the `bytes` and count of `files` of every generation of it on disk (catalog, `-wal`, `-shm`, `-journal`). Only this build uses them.
  - `tables`: the table row files its admissions use, each file counted once, their verified `bytes`, and `sharedBytes`, the part another build's admissions also use.
  - `downloads`: its stored download figure ([download-v1](download-v1.md)): `notMeasured` (no measured manifest), `counting`, `current` or `unavailable`, with `storedObjects` and `storedBytes` when known. Stored objects are shared with every build that lists them.
- `settings.storage.free`: exactly `{productId, buildIdentity}`. Frees that build's file catalog and nothing else: a running or queued preparation is stopped first, every generation's files are deleted and the file catalog becomes `removed`; the next Open, or `inventory.catalog.set` resume, prepares it again from the beginning. Table rows and downloaded objects are kept. The reply has the `freedBytes` and `freedFiles` actually deleted and the file catalog measured after. A build never opened is `PREPARED_DATA_NOT_FOUND`. Cancelled before the change commits, nothing changes; once it commits, the files are deleted whatever the request's cancellation says. `freedBytes` is the files' lengths before the free less what is left, so it matches the `bytes` a list read just before showed. A file that couldn't be deleted is `STORAGE_FREE_INCOMPLETE` (retryable; the message says the list is freed and how many files are left): the catalog stays `removed` and the engine deletes the files later, or at the next free. One free runs at a time; a second waits for it.

## StorageMeasurement

`bytes`, `files` and `skipped` are exact decimal strings. `bytes` is the sum of file lengths under the data folder, every build together; it isn't a build's download size. Links aren't followed. `skipped` counts folders and files that couldn't be read, or lay past the walk's bounds (32 levels, 4096 queued folders); they aren't in the total. `startedAt` and `measuredAt` carry explicit UTC offsets. The folder can change while it's measured, for example during downloads.
