# Library file-footprint samples

`ObjectStore.ForFootprint()` exposes an explicit, disposable `ILibraryFootprint` over a borrowed mixed library. `MeasureAsync` observes file metadata in the captured library directory. Disposing the view cancels and drains its accepted work; the library remains open.

The result reports regular-file counts and logical lengths in disjoint buckets:

| Bucket | Recognized path |
| --- | --- |
| Packs | `packs/pack-NNNNNN.vmp` |
| Database | `objects.sqlite3` |
| DatabaseWal | `objects.sqlite3-wal` |
| DatabaseShm | `objects.sqlite3-shm` |
| Staging | Regular `staging/*.part` files |
| Other | Other regular files, including `library-v1` and `writer.lock` |

Unknown names stay Other. Mixed packs cannot be split into Original and Derived bytes from their names. Total is the checked sum of observed buckets.

Each sample includes its start and completion times. `Complete` means traversal finished without observed gaps; concurrent writers can change files during that interval. Missing required root, packs, staging or database paths make the sample incomplete. Absent optional sidecars and individual pack/staging files can contribute zero. Links, unsupported entries, inaccessible or disappearing paths, directory changes, overflow and resource limits retain bounded typed problems. Truncated diagnostics remain explicit.

The implementation reads metadata through directory descriptors on Linux and macOS x64/arm64, refuses links and uses descriptor-relative traversal. Other platforms return an incomplete `UnsupportedPlatform` result. It never opens payloads or SQLite, checkpoints, creates files, changes ownership, deletes or repairs data.

Defaults bound a measurement to 100,000 entries, depth 16, relative paths of 2,048 characters and 32 problems. Hard limits are 1,000,000 entries, depth 32, relative paths of 4,096 characters and 64 problems; the absolute root also has a 4,096-character cap. Admission fails fast: one measurement per view by default, at most two per view and two across the library. Filesystem metadata work runs on bounded workers; cancellation waits for accepted work to retire.

These are file lengths, not allocated filesystem blocks, verified retention, reclaimable space or an atomic filesystem snapshot. Index accounting remains a separate observation. Subtracting a separately sampled index total cannot establish physical overhead. Registry/catalog database attribution, per-build reporting, imports and app integration remain separate work.
