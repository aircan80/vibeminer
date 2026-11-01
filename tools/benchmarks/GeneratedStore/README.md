# Generated store benchmarks

Developer-only measurements for record files in the layer 1 generated store (#179, [STORAGE](../../../docs/STORAGE.md)). Synthetic table-like rows only (`SyntheticRows.cs` in the Primitives tests: 25 tab-separated text values per row, ids ascending with gaps); it never reads a profile, a game install or the CDN. Each run makes a new folder under the given parent folder (the system temp folder by default) and deletes it afterwards.

```sh
node tools/dotnet.mjs run -c Release --project tools/benchmarks/GeneratedStore -- 200k   # or 1m
node tools/dotnet.mjs run -c Release --project tools/benchmarks/GeneratedStore -- 200k "$PWD/.agents/workspace/bench" cold
node tools/dotnet.mjs run -c Release --project tools/benchmarks/GeneratedStore -- 1m "$PWD/.agents/workspace/bench" tables
```

The optional folder is the parent of the store folder (the system temp folder by default, often tmpfs: put it on a real disk). `cold` flushes the store's files and drops them from the page cache (`posix_fadvise` DONTNEED, Linux, no root) before the reopen, and reports the first 500 pages without a warm-up.

It writes one record file with the default options and reports write time and MB/s, record bytes against stored bytes (blocks, id index, manifest) and the ratio, and id index bytes per record. Then it disposes and reopens the store (page cache warm) and times `RecordFile.OpenAsync`, then p50/p90/p99/max of random 50-record pages (`ReadAsync`) and random `FindAsync` lookups, with 1 caller (20,000 each) and 8 concurrent callers (20,000 each), after a warm-up. Each row of the table also gives KiB allocated per operation and the GCs during it.

`tables` measures layer 5 instead: the same rows as `Db2Row`s (the 25 values split back out, 2% of rows as copies after the physical rows with `CopySourceId` set), stored through `GeneratedTables.StoreAsync`, then after the reopen `GeneratedTables.OpenAsync` and random 50-row `GeneratedTable.ReadPageAsync` and `FindAsync`, the same way.

Wait for other test or benchmark work to finish first, and say which machine, filesystem and cache state a number came from. `RecordFilePageLatencyTests` (Slow) checks the 1 ms p99 page criterion itself on 200k rows, best of 3 rounds. The 1 ms target is for the test machine; on CI (`CI=true`, shared runners) the test only asserts a 5 ms sanity bound and prints the numbers.

## Results

2026-11-01, i9-13900K, btrfs on LUKS on a Samsung 980 PRO NVMe, Release, workstation GC, page cache warm unless noted. Latencies in µs.

### Records

Ratio 2.2x and 0.29 id index bytes per record for both sizes (the synthetic rows have many random ints and floats; real tables likely compress better). With pooled decoders and block buffers, and only the asked-for records copied out:

| records | write MB/s | open first / again | page p50/p99/max, 1 caller | page p50/p99/max, 8 callers | find p50/p99, 1 / 8 callers | KiB/op page, find |
|---|---|---|---|---|---|---|
| 200k | 28.3 | 28.9 / 0.27 ms | 61 / 507 / 2800 | 62 / 141 / 970 | 68/98, 78/110 | 13.5, 5.4 |
| 1M | 38.4 | 31.4 / 0.75 ms | 86 / 433 / 2709 | 66 / 151 / 4305 | 75/104, 87/121 | 13.6, 5.4 |

Before that (a fresh decoder and whole-block arrays per read) a page allocated 43 KiB and a find 35 KiB, and the 8-caller p99s were 663-685 (page) and 603-718 (find). A cold 200k run then gave 262 / 701 / 8539 for the first 500 pages.

### Tables (50-row pages)

Store: 108k rows/s (200k) and 173k rows/s (1M), 1.94x row text to live store bytes. Three runs each; the page p99 with 8 callers is given for every run.

| rows | open first / again | page p50/p99/max, 1 caller | page p99, 8 callers | find p50/p99, 1 / 8 callers | KiB/op page, find |
|---|---|---|---|---|---|
| 200k | 3.1-4.1 / 0.5-1.3 ms | 98-110 / 357-617 / 2918-3102 | 850, 927, 848 | 71/97, 84/114 | 48.3, 6.2 |
| 1M | 7.2-9.0 / 1.2-1.5 ms | 102-114 / 353-518 / 2870-3721 | 857, 1003, 973 | 74/104, 87/122 | 48.4, 6.2 |

Before the pooling and the shared short strings ("", one ASCII character, "10"-"999") a page allocated 95 KiB and a find 36 KiB, with 8-caller p99s of 955-1157 (page) and 690-824 (find). What is left per page is mostly the 50 `Db2Row`s and their 1,250 value strings: about 440 gen0 GCs per 160,000 pages with 8 callers, each pausing all 8 for about 0.75 ms, which is what sets the p99. One caller is well under 1 ms.
