# Settings v1

Capability `settings-v1`. App-wide settings, saved by the engine in its data folder. They apply to every product and build. The desktop never writes the file and never sends a path.

## Methods

- `settings.read`: empty parameters. Returns one `SettingsSnapshot`. Reading never writes the file.
- `settings.set`: `{name:"communityBuildHistory", value:true|false}`. Returns the committed `SettingsSnapshot`, read back from the file after the write. Request cancellation after the file is replaced doesn't withdraw the change. A failed change is never replayed automatically. The last change wins; there is no revision check.
- `settings.storage.measure`: empty parameters. One cancellable request that walks the data folder and returns a `StorageMeasurement`. One walk runs at a time; a second request waits for it. The walk stops at a 5-minute budget with `STORAGE_MEASURE_TIMEOUT`. It sends no progress, reads no file content and takes no download or CPU permits.

## SettingsSnapshot

- `formatVersion`: the settings format this engine writes, `1`.
- `file`: `absent` (no file yet, every value is its default), `read`, `unreadable` or `newer` (a later settings version). `problem` is set for `unreadable` and `newer` (`SETTINGS_VERSION`). An `unreadable` file is either read and found bad (`SETTINGS_UNREADABLE`: bad JSON or UTF-8, wrong shape, no version) or never read (`SETTINGS_NOT_READ`: larger than 64 KiB, changed while read, or not openable, such as a lock or a permission error; retryable except for the size).
- `lastChangedAt`: when the file was last written, with an explicit UTC offset, or null.
- `communityBuildHistory`: `value`, `defaultValue` (false) and `source`: `default` (no saved value), `saved`, or `unreadable` (the saved value, or the whole file, couldn't be used, so the default applies). An unreadable value carries `SETTING_UNREADABLE` or the file's problem. It's never used as if it were saved.
- `storage.dataDirectory`: the engine's absolute data folder, for display only.
- `downloadLimits`: the engine's HTTP permits (`parallelDownloads`, `backgroundDownloads`), `bandwidthCapBytesPerSecond` (null: no cap) and `adjustable` (false in this version).

## File

`settings-v1.json` in the data folder: `{"version":1,"changedAt":"…","values":{"communityBuildHistory":false}}`, at most 64 KiB. A write goes to `settings-v1.json.tmp`, is flushed to disk and then replaces the file, so a failed write leaves the old file as it was (`SETTINGS_WRITE_FAILED`, retryable). Values the engine doesn't know, and top-level fields other than `version`, `changedAt` and `values`, are all kept as written on the next write, however many or deeply nested. A leading UTF-8 byte order mark is ignored. Text that doesn't decode anywhere in the file (invalid UTF-8, a lone surrogate escape) makes it `SETTINGS_UNREADABLE`. The 64 KiB bound is the only parse limit, so any positive integer version above 1, however large, is `newer`. A switch replaces only a file whose bytes were read and found bad (`SETTINGS_UNREADABLE`). A file that was never read is left alone: `settings.set` fails with `SETTINGS_NOT_READ`. A `newer` file is never overwritten: `settings.set` fails with `SETTINGS_VERSION`. The desktop shows the switch disabled with its reason in both cases.

## StorageMeasurement

`bytes`, `files` and `skipped` are exact decimal strings. `bytes` is the sum of file lengths under the data folder, every build together; it isn't a build's download size. Links aren't followed. `skipped` counts folders and files that couldn't be read, or lay past the walk's bounds (32 levels, 4096 queued folders); they aren't in the total. `startedAt` and `measuredAt` carry explicit UTC offsets. The folder can change while it's measured, for example during downloads.
