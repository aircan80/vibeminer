# Object store format 1 (layer 1)

The complete, language-neutral specification of VibeMiner's layer 1 object store: what is on disk, how it is checked, and how a writer and readers behave. An implementation that follows it reads and writes the same files as the reference ([`src/backend/VibeMiner.Objects`](../../src/backend/VibeMiner.Objects/)). Why it is built this way is in [STORAGE](../STORAGE.md#layer-1-the-object-store) and decisions D121-D131 in [DECISIONS](../DECISIONS.md).

Conventions:
- **LE** and **BE** mean little- and big-endian. All integers in packs, entries and history records are little-endian unless a row says BE (BE fields are inside Blizzard formats).
- `u8 u16 u32 u64` are unsigned, `i64` is two's-complement signed.
- Offsets are in bytes from the start of the structure.
- "Unix ms" is milliseconds since 1970-01-01T00:00:00Z.

## 1. Folder layout

The store is one folder, `cdn-store-v1/`, inside the data folder. The folder name carries the store generation: a format that can't be read by this specification gets a new folder name.

**Store kind.** A store is one of two kinds, fixed when it is created:
- **Cdn** (0): CDN bytes, key types 1-6, each checked against its key. This is `cdn-store-v1/`, and everything in this spec applies.
- **Generated** (1): prepared data the app derives itself, key type 7 only, in its own folder (`generated-v1/`). Same format, with these differences: no history log, no staging, and the type check is the length bound only (4.7).

The kind is in every pack header (offset 12) and in the index (`PRAGMA application_id`). The opener says which kind it expects. A folder whose packs or index are of the other kind is refused with `OBJECT_STORE_KIND_MISMATCH` before anything is written. A Cdn store is byte-identical to one written before kinds existed. An older engine refuses a generated folder with packs as newer format (pack offset 12, type 7 entries), but a generated folder with no packs is marked only by `application_id`, which older engines ignore; only the folder name keeps them apart.

| Path | Role |
| --- | --- |
| `writer.lock` | Held with an exclusive OS lock (POSIX `flock(LOCK_EX)`, Windows share-none) for the whole life of the one writer. Its content is unused (empty). |
| `objects.sqlite3` | The index (section 5), with SQLite's `objects.sqlite3-wal` and `objects.sqlite3-shm`. A cache of the packs: everything in it except `last_read` can be rebuilt from them. |
| `objects.sqlite3.rebuild` | Present only while an open rebuilds a missing or damaged index (plus its `-wal`/`-shm`). Deleted at the next open if left behind. |
| `packs/pack-NNNNNN.vmp` | Pack files. `NNNNNN` is the pack number, 6 decimal digits, 000001 upwards. Only that exact 15-character name, with a number of at least 1, counts as a pack; anything else in `packs/` is ignored. |
| `staging/TTNNKKKK….part` | One large object arriving in pieces: `TT` key type and `NN` namespace as 2 lowercase hex digits each, then the 32 lowercase hex digits of the key. |

Nothing else is written there. A reader that finds other files ignores them.

**Pack numbers.** The writer keeps one counter for new packs. At open it starts at one more than the highest of: every pack number in a `packs/` file name (unreadable packs included), listed before open deletes any file, and every `packs.seq` row of the index the open goes on with, read before recovery deletes any row (6.3). After a rebuild at open those are the rebuilt index's rows, which only list readable packs already counted by name; an index that fails during the open's recovery and is then rebuilt may already have raised the counter from its own rows. Each pack roll (6.2) and each compaction target (6.8) takes the next number, so a compaction target is numbered above the active pack, and the active pack isn't always the highest. A number taken by a compaction that then failed isn't handed out again in that session. Over 999,999 is `OBJECT_STORE_LIMIT`.

The counter isn't stored (D373), so a later session can take a number again: a failed compaction target, or an empty pack never listed, that was the highest number and whose file and row are both gone. Such a number never had an `objects` row point at it, so no reader holds a handle, row or checked entry for it. A pack that ever held objects can't be reused: it keeps its file and row until compaction retires it, and its higher-numbered target then holds its objects. Pack numbers are only meaningful inside one store's folder and index; nothing outside the store may refer to one.

## 2. Packs and entries

### 2.1 CRC-32C

Every checksum here is **CRC-32C (Castagnoli)**:
- polynomial 0x1EDC6F41 (reflected 0x82F63B78);
- initial value 0xFFFFFFFF, input and output reflected, final XOR 0xFFFFFFFF;
- check value for the ASCII bytes `123456789`: **0xE3069283**.

`CRC(b)` below means that function over the byte sequence `b`.

### 2.2 Pack file

A pack is a 64-byte header followed by entries back to back, with no padding and no trailer. A pack only grows by appending, except when recovery cuts off a torn end (section 6.3) or a write error cuts back to the last commit.

| Offset | Size | Type | Field |
| --- | --- | --- | --- |
| 0 | 4 | bytes | Magic `VMPK` (0x56 0x4D 0x50 0x4B) |
| 4 | 2 | u16 LE | Format version, 1 |
| 6 | 2 | u16 LE | Header length, 64 |
| 8 | 4 | u32 LE | Pack number; must equal the number in the file name |
| 12 | 4 | u32 LE | Store kind: 0 Cdn, 1 Generated |
| 16 | 16 | bytes | Store id: 16 random bytes chosen when the store was created, the same in every pack of a store |
| 32 | 8 | i64 LE | Created, Unix ms: when this pack file was written (a roll, or a compaction target). Informational; nothing checks it |
| 40 | 20 | bytes | Reserved, all 0 |
| 60 | 4 | u32 LE | `CRC(bytes 0..59)` |

Reading a pack header:
1. Shorter than 64 bytes, wrong magic or wrong CRC: **torn** (unreadable).
2. Otherwise version ≠ 1, header length ≠ 64, store kind > 1, or any reserved byte ≠ 0: **newer format**.
3. Otherwise a pack number different from the file name: unreadable (treated as torn).
4. Otherwise a store kind other than the one the opener expects: `OBJECT_STORE_KIND_MISMATCH`.

### 2.3 Entry

An entry is a 96-byte header, the payload (the object's bytes exactly as served), then the block table:

`entry size = 96 + L + 4 × ceil(L / 65536)`, where L is the payload length.

| Offset | Size | Type | Field |
| --- | --- | --- | --- |
| 0 | 4 | bytes | Magic `VMOE` (0x56 0x4D 0x4F 0x45) |
| 4 | 1 | u8 | Entry format, 1 |
| 5 | 1 | u8 | Key type (section 3) |
| 6 | 1 | u8 | Namespace (section 3) |
| 7 | 1 | u8 | Flags: bit 0 = tombstone; bits 1-7 must be 0 |
| 8 | 16 | bytes | Key |
| 24 | 8 | u64 LE | Payload length L. 0 only for a tombstone; at most 536,870,912 (512 MiB) |
| 32 | 32 | bytes | SHA-256 of the payload (of zero bytes for a tombstone). Local integrity only, not identity |
| 64 | 8 | i64 LE | Stored at, Unix ms |
| 72 | 1 | u8 | Origin: 0 unknown, 1 CDN, 2 shared store |
| 73 | 1 | u8 | Block shift, 16 (blocks of 65,536 bytes) |
| 74 | 2 | u16 LE | Reserved, 0 |
| 76 | 4 | u32 LE | Reserved, 0 |
| 80 | 8 | u64 LE | Store sequence number |
| 88 | 4 | u32 LE | Reserved, 0 |
| 92 | 4 | u32 LE | `CRC(bytes 0..91)` |

**Block table.** Right after the payload come `ceil(L / 65536)` u32 LE values: `CRC` of each 65,536-byte block of the payload in order, the last block short. A tombstone has no payload and no table.

**Sequence numbers** are unique and increase in the order entries are appended, store-wide across packs. Gaps are allowed. Compaction copies entries with their sequence numbers unchanged, so the order of events is the order of sequence numbers, not of pack numbers.

**Tombstones** (flag bit 0, L = 0) record a Delete. They are never removed by compaction, so a rebuild can't bring a deleted object back.

Reading an entry header, in order:
1. Fewer than 96 bytes left in the pack, wrong magic or wrong CRC: **torn**.
2. Otherwise any of these is **newer format**: entry format ≠ 1, a flag bit other than bit 0, block shift ≠ 16, origin > 2, a key type the store's kind doesn't hold (outside 1-6 in a Cdn store, other than 7 in a Generated store), or any reserved byte ≠ 0.
3. Otherwise any of these is **torn**: L > 512 MiB, a tombstone with L ≠ 0, a non-tombstone with L = 0, or the entry size running past the end of the pack.

The namespace isn't checked when reading; Put refuses namespaces other than 0 (section 4).

**Versioning rule.** A header with a valid checksum that this reader doesn't fully understand was written by a newer writer. It is refused as `OBJECT_STORE_NEWER_FORMAT` before anything is written, and it is never truncated.

## 3. Keys

A key is three things: a key type, a namespace byte and a full 16-byte key.

| Type | Name | 16-byte key |
| --- | --- | --- |
| 1 | Config | MD5 of the whole file (build config, CDN config, patch config) |
| 2 | ArchiveIndex | MD5 of the last 28 bytes (the footer) of a native archive `.index` |
| 3 | FileIndex | MD5 of the footer of the loose-file `file-index` |
| 4 | Encoded | The EKey of a BLTE object: MD5 of its BLTE header, or of the whole object when headerless |
| 5 | HistoryDocument | MD5 of one raw version-service reply |
| 6 | HistoryRecord | MD5 of a history record (section 4.6) |
| 7 | Generated | 16 bytes the writer chooses (an identity hash or a content hash). Generated stores only |

Types 0 and 8-255 are spare: Put refuses them, and a reader treats one in an entry header as newer format. A Cdn store treats type 7 the same way, and a Generated store types 1-6.

Namespace 0 is the product's CDN path (`config/` and `data/`; patch configs are type 1 in namespace 0). Namespaces 1-255 are reserved, for `patch/` objects and the product-config path, and refused by Put.

The **index key** is 18 bytes: `[type, namespace, key[0..15]]`. Index rows sort by it, so by type first.

Text form, used in logs: `Type/namespace/32 lowercase hex`, for example `Config/0/3a8e…`.

## 4. Verification per key type

Before anything is appended, an object is checked against its key and its declared length: the length its source declared (EN, an archive index entry, the received length). A config's `*-index-size` hint is never a declared length. A check returns nothing (valid) or a refusal, a code and a message; refusals are never stored.

Order of checks for a whole object in memory (Put, and `Check` in section 10):
1. A key type the store's kind doesn't hold (outside 1-6 for Cdn, other than 7 for Generated): `OBJECT_KEY_TYPE_UNSUPPORTED`. The public `ObjectVerification.Default` is the Cdn check and refuses type 7.
2. A namespace ≠ 0: `OBJECT_NAMESPACE_UNSUPPORTED`.
3. A declared length of 0, or over the type's limit (section 11): `OBJECT_TOO_LARGE`.
4. Bytes not exactly the declared length: `OBJECT_SIZE_MISMATCH`.
5. A declared length over 64 MiB (a whole Put holds at most that; larger encoded objects are staged): `OBJECT_TOO_LARGE`.
6. The type's own check, below.

An incremental check (staging, and `Begin` in section 10) applies 1-3, then feeds bytes in order. More bytes than declared, or fewer at the end, is `OBJECT_SIZE_MISMATCH`.

### 4.1 Config (1) and HistoryDocument (5)

`MD5(all bytes) = key`, else `OBJECT_KEY_MISMATCH`. No grammar is checked here.

### 4.2 ArchiveIndex (2) and FileIndex (3)

The offset width W is 4 for ArchiveIndex and 0 for FileIndex. With `F` the last 28 bytes and `N` the total length:
1. `N < 28`: `OBJECT_INDEX_INVALID`.
2. `MD5(F) ≠ key`: `OBJECT_KEY_MISMATCH`.
3. Footer checksum: take `F[8..27]` (20 bytes), zero its last 8 bytes, MD5 it; the first 8 bytes of that digest must equal `F[20..27]`. Else `OBJECT_INDEX_INVALID`.
4. Footer layout: `F[8]` must be version 1, `F[9]` and `F[10]` 0, `F[11]` the page size in KiB (4), `F[12]` the offset width W, `F[13]` the size width (4), `F[14]` the key width (16) and `F[15]` the checksum width (8). Anything else, an archive-group index with W = 6 included, is `OBJECT_INDEX_UNSUPPORTED`.
5. Geometry:
   - entries `E = u32 LE at F[16..19]`; record size `R = 16 + 4 + W`;
   - entries per page `P = floor(4096 / R)`: 170 for W = 4, 204 for W = 0;
   - pages `G = ceil(E / P)`;
   - `N` must equal `G × 4096 + G × 24 + 28`, else `OBJECT_INDEX_INVALID`.
6. TOC: the `G × 24` bytes at offset `G × 4096`, in two halves:
   - first `G × 16` bytes: the last key of each page, in page order (not checked here);
   - then `G × 8` bytes: the first 8 bytes of `MD5(page)` for each page, in page order.

   The first 8 bytes of `MD5(whole TOC)` must equal `F[0..7]`, else `OBJECT_INDEX_INVALID`.
7. Every page p (4,096 bytes at `p × 4096`): the first 8 bytes of `MD5(page)` must equal the 8 bytes at TOC offset `G × 16 + p × 8`, else `OBJECT_INDEX_INVALID`.

An index with 0 entries is exactly its 28-byte footer and is valid. Entry ordering inside pages is not checked here; the hashes prove the bytes are the ones served.

### 4.3 Encoded (4): BLTE

First 8 bytes: `BLTE`, then the header length `H` as u32 BE. Not `BLTE`: `OBJECT_BLTE_INVALID`.

**Headerless (H = 0):**
- The object must be at least 9 bytes (the prefix and one chunk mode byte), else `OBJECT_BLTE_INVALID`.
- `MD5(whole object, prefix included) = key`, else `OBJECT_KEY_MISMATCH`, decided at the end.
- There is no partial proof, so a staged headerless object resumes from 0.

**Chunked (H > 0):**
1. `H` must be between 36 (12 + 24) and 12 + 24 × 65,536, and less than the declared length. Else `OBJECT_BLTE_INVALID`.
2. `MD5(bytes 0..H-1) = key`, else `OBJECT_KEY_MISMATCH`.
3. Byte 8 (table format) must be 0x0F, else `OBJECT_BLTE_UNSUPPORTED`.
4. Chunk count `C = u24 BE at bytes 9..11`, and `H = 12 + 24 × C` with `C ≥ 1`. Else `OBJECT_BLTE_INVALID`.
5. Chunk descriptor i, at `12 + 24 i`:
   - u32 BE encoded size, which must be ≥ 1, else `OBJECT_BLTE_INVALID`;
   - u32 BE decoded size, not checked;
   - 16 bytes MD5 of the chunk's encoded bytes.
6. `H + Σ encoded sizes` must equal the declared length, else `OBJECT_SIZE_MISMATCH`.
7. The chunks follow in order. The MD5 of each chunk's encoded bytes (mode byte included) must equal its descriptor, else `OBJECT_BLTE_INVALID`, decided as soon as the chunk's last byte arrives.
8. **Proven length:** `H` plus the sizes of the chunks proven so far, and 0 until the header is proven.

Checks 1-6 are public as `BlteChunkTable.Prove` (plus `ReadHeaderLength` for the prefix and `CheckChunk` for check 7 on one chunk), so layer 2 can prove a range of a large loose object chunk by chunk without storing it ([D340](../DECISIONS.md)). It applies no store size limit.

Chunk modes (`N`, `Z`, `E`, others) are opaque. An encrypted (`E`) chunk is checked by its MD5 like any other and stored as served; nothing is decrypted, and nothing is zero-filled.

### 4.4 HistoryRecord (6)

1. `MD5(record) = key`, else `OBJECT_KEY_MISMATCH`.
2. The record follows the grammar below, else `OBJECT_HISTORY_INVALID`.
3. Put only: its document (type 5, namespace 0) must already be stored, or come earlier in the same batch. Else `OBJECT_HISTORY_DOCUMENT_MISSING`. This depends on the store, not the bytes, so a bytes-only check doesn't report it.

### 4.5 Spare types

Types the store's kind doesn't hold are refused (`OBJECT_KEY_TYPE_UNSUPPORTED`). Adding a type later doesn't change this format; readers that predate it treat it as newer.

### 4.6 History record format 1

| Offset | Size | Type | Field |
| --- | --- | --- | --- |
| 0 | 1 | u8 | Version, 1 |
| 1 | 1 | u8 | Kind: 1 versions, 2 cdns, 3 bgdl, 4 summary, 5 definition, 6 listfile release, 7 listfile part, 8 listfile delta |
| 2 | 2 | u16 LE | Reserved, 0 |
| 4 | 8 | i64 LE | Observed at, Unix ms |
| 12 | 16 | bytes | Document key (the MD5 of the reply bytes) |
| 28 | 1 | u8 | Product length p: 0 for a summary, else 1-64 |
| 29 | p | ASCII | Product, characters `a-z 0-9 _ -` |
| 29+p | 1 | u8 | Region length r, 1-16 |
| 30+p | r | ASCII | Region, characters `a-z 0-9` |
| 30+p+r | 2 | u16 LE | URL length u, 0-1024 |
| 32+p+r | u | UTF-8 | Request URL, strict UTF-8 |

The record must end exactly after the URL. The shortest is 33 bytes (a summary) and the longest 1,136. A summary (`/v2/summary`) lists every product, so it has none; any other kind with an empty product, or a summary with one, fails the grammar. Kind 4 was added before the format's first release (D152). Kind 5 is a WoWDBDefs table definition: the product is the table id (8 lower hex), the region `wowdbdefs` and the URL the raw `.dbd` at its commit; it was also added before the first release (D181). Kinds 6 and 7 are the wow-listfile, also added before the first release (D220): kind 6 is the latest release (product `wowlistfile`, region `latest`, URL the GitHub releases/latest API URL, reply the release JSON as served); kind 7 is one fixed 8 MiB part of that release's CSV (product the release tag, region `p` + the part index in decimal without leading zeros, 0-63, URL the asset's releases/download URL, reply bytes [index * 8 MiB, min((index + 1) * 8 MiB, size)) of the asset). Kind 8 (#186) is a listfile delta: one release recorded against the release before it (product the release tag, region `delta`, URL the asset's releases/download URL as for its parts, reply a delta document in layer 4's format, [listfile-delta.md](listfile-delta.md)); layers 1 and 2 never read the reply. Its document is at most 16 MiB like any other.

`u` counts bytes, not characters. **Strict UTF-8** means well-formed UTF-8 as Unicode defines it: no overlong forms, no surrogates (U+D800-U+DFFF), nothing above U+10FFFF, no truncated sequence. Anything well-formed is accepted, including NUL, other control characters and a leading BOM (EF BB BF), which is kept as U+FEFF, not stripped. The URL is never checked as a URL. The reference writer refuses a URL string with an unpaired surrogate (an argument error, before anything is written) rather than storing U+FFFD in its place.

Product and region bytes outside their character sets fail the grammar.

### 4.7 Generated (7)

No key check: the key is the writer's choice. Only checks 1-5 of section 4 apply, so the length is 1 B to 64 MiB and the whole object is in memory. The entry SHA-256 and block CRCs guard the bytes as for every entry; a full Verify of a generated object is the SHA-256 alone.

## 5. `objects.sqlite3`

SQLite 3, WAL journal, `PRAGMA user_version = 1`. `PRAGMA application_id` is the store kind: 0 (never set) Cdn, 1 Generated. The writer runs `synchronous=NORMAL` and switches to `FULL` around the commits listed in section 6.12. Exact DDL:

```sql
CREATE TABLE store(id INTEGER PRIMARY KEY CHECK(id = 1), store_id BLOB NOT NULL CHECK(length(store_id) = 16),
    format INTEGER NOT NULL, created_at INTEGER NOT NULL, next_seq INTEGER NOT NULL,
    object_count INTEGER NOT NULL, object_digest INTEGER NOT NULL, history_count INTEGER NOT NULL, history_digest INTEGER NOT NULL,
    clean_close INTEGER NOT NULL, row_check INTEGER NOT NULL);
CREATE TABLE packs(seq INTEGER PRIMARY KEY, state INTEGER NOT NULL, committed_length INTEGER NOT NULL, live_bytes INTEGER NOT NULL,
    row_check INTEGER NOT NULL);
CREATE TABLE objects(k BLOB PRIMARY KEY, pack INTEGER NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL,
    sha256 BLOB NOT NULL, last_read INTEGER) WITHOUT ROWID;
CREATE TABLE history(record BLOB PRIMARY KEY, observed_at INTEGER NOT NULL, product TEXT NOT NULL, region TEXT NOT NULL,
    kind INTEGER NOT NULL, document BLOB NOT NULL, url TEXT NOT NULL) WITHOUT ROWID;
CREATE INDEX history_order ON history(product, region, kind, observed_at, record);
CREATE INDEX history_time ON history(observed_at, record);
```

### 5.1 Columns

| Table.column | Meaning |
| --- | --- |
| `store.id` | Always 1: the table has exactly one row |
| `store.store_id` | The store id in every pack header |
| `store.format` | 1 |
| `store.created_at` | Unix ms when this index was created: the store's creation for a new store, the rebuild time after a rebuild at open (6.3 step 5); a Scan on a running store (6.6) keeps it. Informational: only `row_check` covers it, and it is never compared with a pack header's Created, which differs anyway |
| `store.next_seq` | The sequence number the next entry gets: at least one more than any committed entry's |
| `store.object_count`, `object_digest` | The number of `objects` rows, and the XOR of their row values (5.3) |
| `store.history_count`, `history_digest` | The same over `history` rows |
| `store.clean_close` | 1 when the last writer closed cleanly (everything committed), 0 from the moment a writer opens. It decides how much an open checks (6.3) |
| `store.row_check` | `CRC` of the store row (5.2) |
| `packs.seq` | Pack number |
| `packs.state` | 0 active (the one being appended to; at most one), 1 sealed, 2 compacting (a compaction target not yet switched in), 3 retired (replaced by compaction, its file about to be deleted) |
| `packs.committed_length` | Bytes of the pack covered by committed rows; anything past it is the tail |
| `packs.live_bytes` | Sum of entry sizes of the pack's live rows, plus 96 per tombstone it holds; used to choose packs for compaction. Not covered by `row_check` |
| `packs.row_check` | `CRC` of the pack row (5.2) |
| `objects.k` | 18-byte index key (section 3) |
| `objects.pack`, `offset` | Where the entry's header starts |
| `objects.length` | Payload length L |
| `objects.sha256` | The entry header's SHA-256 |
| `objects.last_read` | Unix day (`floor(unix seconds / 86400)`) of the last read, or NULL; best effort; not rebuilt by Scan unless carried over |
| `history.record` | The record's 16-byte key |
| `history.observed_at`, `product`, `region`, `kind`, `document`, `url` | The record's fields; `document` is the 16-byte document key |

### 5.2 Row checks

All integers are written as i64 LE, then `CRC` is taken over the concatenation. The result, 0 to 2^32−1, is stored as an INTEGER.

- `packs.row_check = CRC(seq ‖ state ‖ committed_length)`, 24 bytes.
- `store.row_check = CRC(store_id[16] ‖ format ‖ created_at ‖ next_seq ‖ object_count ‖ object_digest ‖ history_count ‖ history_digest ‖ clean_close)`, 80 bytes.

Every write to these rows recomputes `row_check` in the same statement.

### 5.3 Totals digest

`Mix(b)` turns a byte string into a signed 64-bit value. Arithmetic is modulo 2^64:
1. `x = (CRC(b) << 32) | CRC(reverse(b))`, where `reverse` is the bytes in reverse order.
2. `x = x + 0x9E3779B97F4A7C15`
3. `x = (x XOR (x >> 30)) × 0xBF58476D1CE4E5B9`
4. `x = (x XOR (x >> 27)) × 0x94D049BB133111EB`
5. `x = x XOR (x >> 31)`, read as i64.

Row values:
- Object row: `Mix(k[18] ‖ i64 LE pack ‖ i64 LE offset ‖ i64 LE length ‖ sha256[32])`.
- History row: `Mix(record[16] ‖ document[16] ‖ i64 LE observed_at ‖ i64 LE kind ‖ UTF-8(product + "\n" + region + "\n" + url))`.

A table's digest is the XOR of its row values (0 for an empty table). It is updated incrementally: XOR a row's value in when it is inserted, out when it is deleted, and both out and in when compaction moves it.

## 6. Algorithms

### 6.1 Put and group commit

**Put(batch, commitNow)**
1. The batch must be at most 4,096 objects and 256 MiB in total, else `OBJECT_STORE_LIMIT` (thrown).
2. Check each object (section 4). A refused object gets its code and goes no further.
3. Compute each object's SHA-256 and block table, then hand the batch to the writer queue.
4. For each object, in order, the writer:
   - marks it Canceled if the caller canceled before it got here;
   - marks it AlreadyPresent if its key has a committed row or is pending in the current batch;
   - refuses a history record whose document is neither stored nor pending (`OBJECT_HISTORY_DOCUMENT_MISSING`);
   - otherwise makes room (6.2), writes the entry at the end of the active pack with the next sequence number, and holds it pending.
5. Put returns when every object it appended is committed. An appended object is committed even if the caller canceled meanwhile.

**Closing a batch.** A batch closes when any of these holds:
- 128 pending objects, or 16 MiB of pending entry bytes;
- after a request is processed, its oldest pending entry has waited 250 ms;
- the writer has nothing else queued;
- the request asked for `commitNow`, or a pack roll, Delete or maintenance needs it.

**Closing, in this order:**
1. `fsync` the active pack.
2. One SQLite transaction, `synchronous=NORMAL`:
   - insert the rows (an object row, and a history row when a history record is inserted);
   - remove the rows tombstones delete;
   - set the pack's `committed_length` to the end of the last entry, with its `row_check`;
   - add to `live_bytes`;
   - set `next_seq` and the totals in the store row, with its `row_check`;
   - record queued read days in `last_read`.
3. Commit.

Only now are the objects visible to Has and Read, and their Puts complete.

**On a write or SQLite error,** every pending caller fails (`OBJECT_STORE_WRITE_FAILED`, or `OBJECT_STORE_DISK_FULL`) and the pack is cut back to `committed_length` and synced. Callers hear of the error only after the cut, so a failed Put never sees the pack longer than its last commit. If that cut fails too, the writer stops until the store is reopened: the failed and queued callers, and every later write, get the same error, nothing past `committed_length` is served, and the next open recovers the uncut tail (6.3) as it would after a crash.

### 6.2 Pack roll

There is no active pack, or the next entry would take the pack past its limit (2 GiB by default) while the pack already holds an entry. Then:
1. Close the pending batch, if any.
2. Create the pack file with the next pack number (section 1) and its header, `fsync` it, and `fsync` the `packs/` folder.
3. One transaction with `synchronous=FULL`: mark the old active pack sealed, and insert the new pack as active with `committed_length` 64. Both rows get their row checks.

An entry larger than the limit goes alone into a fresh pack.

### 6.3 Open, validation, recovery

**Writer open:**
1. Create `packs/` and `staging/`. Take `writer.lock`, else `OBJECT_STORE_BUSY`.
2. Read `user_version` without writing. If it can't be read, the index is unusable. A value other than 1 is a **damaged version** when it is 1 with one bit flipped (0, 3, 5, 9, ... and -2^31 + 1, the sign bit flipped), or any value below 1, and the rows of `sqlite_master` (`type`, `name`, `tbl_name`, `sql`) are exactly those the DDL of section 5 creates, with nothing extra. A damaged version makes the index unusable. Any other value above 1 is `OBJECT_STORE_NEWER_FORMAT`. A later index format changes the schema as well as `user_version`, so it is never taken for a damaged version. If it is 1 or damaged, read `application_id`: above 1 is `OBJECT_STORE_NEWER_FORMAT`, the other kind `OBJECT_STORE_KIND_MISMATCH`.
3. Read every `pack-*.vmp` header (2.2):
   - any newer header: `OBJECT_STORE_NEWER_FORMAT`;
   - a valid header of the other kind: `OBJECT_STORE_KIND_MISMATCH`;
   - valid headers from two store ids: `OBJECT_STORE_FOREIGN_PACK`.
4. If `user_version` = 1, **validate** the index on a read-only connection. Every open runs the **cheap check**. The index is unusable when:
   - for any of `store`, `packs`, `objects` and `history`, the column names that `PRAGMA table_info` returns aren't exactly those of section 5, in order (a missing table returns none). Only names are compared: not declared types, constraints, `WITHOUT ROWID` or the two `history` indexes. Extra tables and indexes are allowed;
   - there isn't exactly one store row, or that row has a wrong type (every column INTEGER except `store_id`, a BLOB), `id` ≠ 1, a `store_id` that isn't 16 bytes, `format` ≠ 1, `next_seq` < 1, `clean_close` outside 0-1, or a bad `row_check`;
   - a pack row has a column that isn't INTEGER, `seq` < 1, a state outside 0-3, `committed_length` < 64, or a bad `row_check`, or more than one pack is active (`live_bytes` must be an INTEGER, any value);
   - its store id differs from the one the packs carry.

   When the cheap check passes but `clean_close` is 0 (the last writer didn't close cleanly), the open also runs the **full check**. The index is then also unusable when:
   - `PRAGMA integrity_check` isn't `ok`. This also finds rows out of key order; `quick_check` does not.
   - recounting the `objects` rows and recomputing their digest (5.3) disagrees with the store row, or the same for `history`. Both sides are read in one snapshot, so the check is exact while a writer commits (6.13, "Measuring a live index").

   The full check costs about 0.8 s per million rows here, the cheap one milliseconds. A writer can also run the full check on request (Scrub, 6.13).
5. **Unusable index, or none** (`user_version` below 1, or missing):
   - With no readable pack, delete the index files and any pack file of at most 64 bytes, then create a fresh index: a random store id, `next_seq` 1, zero totals.
   - Otherwise rebuild it (6.6) into `objects.sqlite3.rebuild`. Checkpoint it, delete the old index's `-wal`/`-shm`, move the rebuilt file over `objects.sqlite3`, and `fsync` the folder.
6. If nothing was rebuilt, walk the active pack's tail headers (from `committed_length`) without writing. A newer entry there means `OBJECT_STORE_NEWER_FORMAT`. Then delete leftover `.rebuild` files.
7. Open the writer connection, load the store row, and set `clean_close` to 0 in a `synchronous=FULL` transaction, before anything else is changed. From here on a crash leaves the marker unset, so the next open runs the full check.
8. Clean up the packs:
   - For each pack in state compacting or retired, delete its file, `fsync` `packs/`, and delete its row.
   - A pack file with no row and at most 64 bytes is deleted; any other unlisted pack is reported and left alone.
   - A listed pack whose file is missing is reported; its objects read Lost, and an active one is sealed.
   - A sealed pack shorter or longer than `committed_length` is reported and left alone.
   - An active pack shorter than `committed_length` is reported and sealed.
   - If the active pack is longer than `committed_length`, recover its tail (below).
9. If step 7 or 8 fails with an index error, nothing was rebuilt yet and at least one pack is readable, close everything, rebuild (step 5), and run steps 7 and 8 once more.
10. Delete staged files not written for 7 days, and start the writer.

**Tail recovery** walks the active pack from `committed_length`, entry by entry:
1. A newer header: `OBJECT_STORE_NEWER_FORMAT`. A torn header: stop.
2. Check the entry in full: SHA-256, block table, then Put's checks 1-3 and 6 of section 4 (namespace, the type's length bound and the key type's own check). A tombstone has no bytes, but its SHA-256 must still be that of zero bytes.
3. If the header is sound but the bytes fail (SHA-256 or blocks), skip the entry and remember it. It doesn't count towards `next_seq`. When a later sound entry follows, the damaged ones stay in the pack, reported and not indexed.
4. An entry that fails those Put checks, or a history record whose document isn't stored, is **refused**. It stays in the pack unindexed; after the walk it gets a tombstone (see "after the walk").
5. A tombstone:
   - if its key has a copy earlier in this tail, that copy is dropped;
   - otherwise, if the key has a committed row, the row is removed;
   - either way, it counts 96 kept bytes.
6. A copy of a key that is already stored or already recovered: skipped. The first copy wins.
7. Anything else is recovered.

After the walk:
- **Cut.** The pack is cut after the last sound entry: one whose header reads valid, fits in the file, and whose SHA-256 and blocks pass. An entry refused in step 4 counts as sound. So a torn end goes, and so do damaged entries that no sound entry follows. The cut is `fsync`ed. `next_seq` becomes at least one more than the highest sequence number of a sound entry.
- **Refused keys.** Each refused key gets a tombstone appended. If a sound later copy of that key was recovered, it is taken out of the batch and appended again after the tombstone: payload at most 64 MiB, origin kept. This puts the refusal in the pack, so a rebuild agrees with recovery whatever checks it runs, including a history record whose document was missing, which Scan alone would keep (6.6).
- **Commit.** The recovered rows commit as one batch (6.1).

### 6.4 Read

**Whole read (key):**
1. Look up the row. None: Absent.
2. A row that can't name an entry is Corrupt: pack < 1, offset < 64, length outside 1-512 MiB, or a SHA-256 that isn't 32 bytes.
3. Length over 64 MiB: `OBJECT_STORE_LIMIT`, thrown (read by range instead).
4. Open the pack. If it's missing, look the row up once more (compaction may have moved it); still missing: Lost.
5. Read the whole entry in one read. Short, a header that doesn't read valid, or a header that disagrees with the row (key, length, SHA-256, a tombstone): Corrupt.
6. Check every block's CRC. Then, if L ≤ 1 MiB and this object hasn't been checked in this session, check its SHA-256. The session set holds at most 1,048,576 entries and is cleared when full.
7. Return the payload, marked Present. Queue today's day for `last_read`, at most 65,536 queued keys.

Corrupt and Lost never return bytes.

**Range read (key, offset, n):**
1. `offset < 0` is the caller's error; `n` over 64 MiB is `OBJECT_STORE_LIMIT`.
2. Steps 1, 2 and 4 of a whole read.
3. Read the 96-byte header and check it against the row as in step 5.
4. `offset + n > L` is the caller's error.
5. For blocks `b0 = floor(offset / 65536)` to `b1 = floor((offset + n − 1) / 65536)`, read their table entries and their bytes, and check each block's CRC. Any mismatch: Corrupt, nothing copied.
6. Copy the range. Nothing outside those blocks is read.

### 6.5 Delete (keys)

For each key:
- A history type: Refused (`OBJECT_DELETE_REFUSED`).
- Pending in the current batch: close the batch first.
- No row: Absent.
- Otherwise append a tombstone with the next sequence number.

The batch closes at the end of the request, removing the rows, their history rows, and their value from the totals, in that transaction.

### 6.6 Scan (rebuild from the packs)

On a running store:
1. Seal the active pack (a `synchronous=FULL` transaction).
2. In one transaction on the live index:
   - save every non-NULL `last_read` aside, then empty `objects`, `history` and `packs`;
   - replay (below);
   - put the saved `last_read` values back;
   - commit, then checkpoint.

Readers in every process see the old index until the commit.

At open, the replay runs into a new file instead (6.3, step 5).

**Replay:**
1. Open every pack file with a valid header, listed in `packs` or not (all with one store id, else `OBJECT_STORE_FOREIGN_PACK`), and walk each from offset 64 to the file's length. `committed_length` isn't used, so whole entries past it in a sealed pack are replayed too.
2. Merge the walks by `(sequence number, pack number)`, ascending.
3. For each entry, check its SHA-256 and block table, then the checks Put makes (section 4, 1-3 and 6: namespace 0, the type's length bound, the key type's own check), as tail recovery does. A tombstone's SHA-256 must be that of zero bytes; it has no key check. An entry that fails is left out and reported, corrupt when its SHA-256 or blocks fail, unverifiable when a Put check does. Either way it still counts for `next_seq` and for its pack's `committed_length`, and it doesn't stop a later copy of its key.
4. A tombstone removes its key's row and history row, and counts 96 kept bytes for its pack.
5. A key that already has a row is a duplicate, skipped.
6. Anything else inserts its row. A history record also inserts its history row; one that doesn't parse is left out and reported.
7. A newer header refuses the Scan (`OBJECT_STORE_NEWER_FORMAT`). A torn header ends that pack's walk; the rest of that pack is unreachable.
8. Insert every walked pack as sealed. Its `committed_length` is where its walk stopped: the end of the last entry whose header read valid and whose size fit in the file, whether or not its bytes passed step 3, or 64 when there is none. Bytes past a torn header stay in the file, so that pack is longer than `committed_length`, which the next open reports (6.3 step 8). Then:
   - set `live_bytes` from its rows' entry sizes plus its tombstones;
   - set `next_seq` to the highest sequence number of any walked entry + 1 (1 with none);
   - measure the totals from the rows written.

Scan keeps the store row's `store_id` and `created_at`; a rebuild at open takes `store_id` from the packs.

**Problems Scan reports**, as text in its report:
- a pack file whose header isn't valid: skipped (it gets no row);
- a torn entry header: the rest of that pack is unreachable;
- an entry that fails step 3: left out. Only these stop being listed once the list holds 100 problems; they are always counted;
- on a running store, each pack the old index didn't list, and each listed pack walked past its `committed_length`, with the bytes beyond it. These are informational: the replay is the same either way;
- a history record that doesn't parse: left out, counted as unverifiable;
- the number of history rows whose document has no `objects` row. These rows stay.

Its counts: packs walked, entries, live rows, duplicates, tombstones, corrupt and unverifiable entries.

Afterwards the next Put opens a new pack. Scan rebuilds the index the writer kept: the same object and history rows, apart from `last_read`. That holds while every whole entry sits within a listed pack's `committed_length`.

**Entries the writer never indexed.** Whole entries past `committed_length` in a sealed pack, and every entry in a pack with no row, were never committed by this index. Sealed packs only get such bytes by damage or an index from another time (a lost or replaced `objects.sqlite3`), and a pack gets no row only the same way: a writer lists a pack before it appends to it. Scan treats them like a tail at open: synced entries that pass every check Put makes are indexed, tombstones apply, and the rest is left out and reported. It never relies on `committed_length` or on the old pack list to decide, because a rebuild at open has neither, and because Scan itself then sets `committed_length` past whatever it left out, so trusting it would let a refused entry in on the next Scan. A history record whose document no copy holds is still kept and counted as dangling (above), where tail recovery refuses it.

### 6.7 Verify (full)

1. Page through `objects` by key, 1,024 rows at a time. Each page must move past the cursor; a key not above the previous one is `OBJECT_STORE_INDEX_UNREADABLE`.
2. Within a page, by pack and offset, check each row as a read would: header against the row, every block CRC and the SHA-256, plus the checks Put makes (namespace, length bound, key type) when `full`. A row whose bytes are sound but that Put would refuse (a namespace other than 0 or a length over its type's limit, which a Scan before D372 could index) is Corrupt only under `full`.
3. A failure counts only if the row still points where it did (compaction may have moved it).
4. Report the counts of checked, Corrupt and Lost objects, with at most 1,000 keys of each.

### 6.8 Compact

Candidates are sealed packs where:
- `dead = committed_length − 64 − live_bytes` is greater than 0;
- `dead` is at least the threshold fraction (25% by default) of `committed_length − 64`;
- the pack isn't already known in this session to hold a corrupt entry.

The most dead bytes go first, up to a maximum number of packs.

For each pack:
1. Close the pending batch.
2. Insert the target pack (the next pack number, section 1, so above the active pack) as compacting, in a `synchronous=FULL` transaction.
3. Create the target with its header.
4. Walk the source up to `committed_length`:
   - every tombstone is copied;
   - an entry is live only if its row points at exactly this pack and offset, and only live entries are copied;
   - each copy writes the payload (checking its SHA-256 and blocks as it goes), then its table, then its original header, unchanged;
   - a torn or newer header aborts.
5. On a corrupt live entry or an abort, delete the target file and its row and leave the source as it is. A corrupt entry also counts the pack as skipped.
6. `fsync` the target and the `packs/` folder.
7. **Switch**, in one `synchronous=FULL` transaction:
   - move each live row to its new offset in the target;
   - mark the target sealed, with its `committed_length`, its `live_bytes` (entries plus tombstones) and its row check;
   - mark the source retired;
   - update the store row's object digest for every moved row.
8. Delete the source file and `fsync` `packs/`.
9. Delete the source's row.

Cancellation or a failure before the switch deletes the target file and its row.

### 6.9 Staging (large encoded objects)

**Start(key, declared length):**
1. The store must be Cdn (a Generated store has no staging), the key Encoded, the namespace 0, and the declared length 1 B to 512 MiB.
2. At most 16 stagings may be open, with at most 2 GiB declared in all (`OBJECT_STORE_LIMIT`), and one per key.
3. Open or create the staged file.
4. Resume: check what is already in the file and keep only its proven length (4.3). The rest, which may be garbage written before a crash, is cut off.

**Append(bytes):** write them at the current length, then feed them to the check. If the check fails, cut the file back to the proven length and fail with `OBJECT_STAGING_REJECTED`. After that the staging must be started again.

**Commit:**
1. The length must equal the declared length.
2. Close the file and hand it to the writer. The writer:
   - copies the file into the pack in one pass that runs the whole check of 4.3 again and computes the SHA-256 and block table;
   - writes the payload, then the table, then the header last;
   - closes the batch at once.
3. A failed check, or a file that can't be read, refuses only this object (`OBJECT_STAGING_REJECTED` or the check's code). The pack is cut back to where the entry began, and the staged file is deleted.

**Dispose** keeps the file for a later resume. **Abandon** deletes it.

### 6.10 Read-only second store

**Open:**
1. The index must exist, else `OBJECT_STORE_MISSING`. A damaged `user_version` (6.3 step 2) is `OBJECT_STORE_INDEX_UNREADABLE`, after the pack headers of step 2 are checked. Otherwise a `user_version` above 1 is `OBJECT_STORE_NEWER_FORMAT`; below 1 is `OBJECT_STORE_MISSING`. The `application_id` is checked as in 6.3 step 2.
2. Read every pack header: a newer one is `OBJECT_STORE_NEWER_FORMAT`, the other kind `OBJECT_STORE_KIND_MISMATCH`, two store ids are `OBJECT_STORE_FOREIGN_PACK`.
3. Validate the index as in 6.3 step 4: the cheap check, and the full one when `clean_close` is 0. A running writer keeps the marker at 0, and a reader can't tell that from a crash, so a reader opening a store whose writer runs pays the full check. Unusable is `OBJECT_STORE_INDEX_UNREADABLE`, since a read-only store can't rebuild.
4. Report the open: `FullIndexCheck` says whether step 3 ran the full check. Recovered entries and truncated bytes are 0, `IndexRebuilt` is false and there are no problems, since the open never writes and fails rather than reporting a problem.

**Then:**
- It offers Has, Read, ReadRange, the history read and Verify, and nothing that writes.
- It never takes the lock, recovers, records reads or writes a byte.
- It sees exactly what that store's writer has committed.
- SQLite may create the index's `-wal` and `-shm`, except on read-only media (below).

**Read-only media.** The reference opens the index with `SQLITE_OPEN_READONLY` (Microsoft.Data.Sqlite `Mode=ReadOnly`). A WAL database opened that way needs its `-wal` and `-shm` to exist already or to be creatable, and after a clean close SQLite has deleted both. So when that first open fails because SQLite can't create them (`SQLITE_CANTOPEN` or `SQLITE_READONLY`), the folder can't be written (POSIX `access(W_OK)` fails, so a failure from running out of file handles never counts; Windows, parked, skips this check), and `-wal` is missing or empty, the main file holds every commit, and the store opens the index with `immutable=1` (a `file:` URI) for as long as it is open, for reads, Verify and the read-only scrub alike. It then also requires `clean_close` = 1 after the cheap check, else `OBJECT_STORE_INDEX_UNREADABLE`: the last writer checkpointed and closed, and a writer that ran since would have left a `-wal`. With `immutable=1` SQLite neither reads the `-wal` nor sees later commits, which is why:
- a `-wal` with bytes in it but no creatable `-shm` (a copy taken while a writer ran) is `OBJECT_STORE_INDEX_UNREADABLE`, never read without its commits;
- the immutable open is taken whenever this process can't create the files, so a writer that can write a folder its readers can't is not supported: those readers wouldn't see its later commits.

Nothing changes for a reader that can create the files: it opens normally and sees what the running writer commits. A read-only store never touches `writer.lock`, so a lock that can't be taken on read-only media doesn't matter.

A layered reader asks the profile's own store first and the second store only for keys the first reports Absent.

### 6.11 History log

**Append(observation):**
1. Compute the document key `D = MD5(reply)`.
2. Build the record (4.6) and compute its key `R = MD5(record)`.
3. Put `[document D, record R]` as one batch with `commitNow`. In a Generated store both are refused (`OBJECT_KEY_TYPE_UNSUPPORTED`), so it has no history.

The same reply observed again is a new record sharing the document. The same observation twice is one entry. History entries are never deleted.

**Read(query):**
- filters: product, region and kind;
- order: observed time, then record key, ascending;
- paging: a page continues strictly after the query's `after` entry, 1 to 10,000 entries per page;
- a page that doesn't run forward is `OBJECT_STORE_INDEX_UNREADABLE`.

### 6.12a Close

A writer closes after its queue is empty and every pending batch has committed:
1. Set `clean_close` to 1, in a `synchronous=FULL` transaction.
2. Checkpoint the WAL (`wal_checkpoint(TRUNCATE)`).
3. Release the lock.

A writer that stopped on an error, or a process that is killed, never sets the marker, so the next open checks fully.

### 6.13 Scrub

The full check on request, for a scheduler above layer 1 to run in the background; this layer doesn't schedule it.
1. Run the full check of 6.3 step 4 on reader connections, while writes go on (see "Measuring a live index" below).
2. If the check fails, confirm it as a writer request, then rebuild the index in place as Scan does (6.6) and report the problem.
3. Then run Verify (6.7) over every object.

The scrub honours cancellation: it interrupts SQLite's running statements.

A read-only store runs the same check and Verify and reports a failing index without rebuilding it.

**Measuring a live index.** A writer may commit while the full check runs, so the totals and the rows must come from one snapshot:
1. Begin a read transaction on the check's connection and read the store row in it. That fixes the snapshot.
2. Take it with `sqlite3_snapshot_get`. Each parallel slice connection reads once (so its pager has opened the WAL), begins a read transaction and opens the same snapshot with `sqlite3_snapshot_open`, then measures its key range.
3. Measure `history` on the check's connection, and compare both against the store row read in step 1.
4. If the snapshot can't be taken or a slice can't open it, measure everything on the check's connection inside its transaction instead: slower, still exact. Never compare a mixed view.

Commits made meanwhile are in neither side, so the check always concludes; nothing is retried and the writer is never held to measure. The cost: while the transaction is open, checkpoints can't backfill past it, so the WAL grows by the commits made during the measure. An immutable index (6.10) has no WAL and no writer; its slices read the file as it is. So does a check run with the writer lock held (a writer's open, the confirm before a rebuild): nothing can commit, and `sqlite3_snapshot_get` fails anyway on a WAL with no frames since the `-shm` was built, as before a writer's first commit.

**Which damage is found when.** Pack bytes are checked by every read (block CRCs), so damage done while the app was closed never returns bad bytes. Index damage after a clean close is found by the cheap check when it hits the store or pack rows. Rows dropped, added, altered or out of order are found:
- by the next scrub or explicit full check;
- by the next open after an unclean shutdown;
- for a damaged row that is read, at that read (Corrupt);
- for rows out of order, by Verify's cursor check (`OBJECT_STORE_INDEX_UNREADABLE`).

Until then a lookup can miss such a row and report the object Absent. Fetching and putting it again does not repair this: the Put also misses the hidden row, and its insert can then hit the primary key, so that batch fails with `OBJECT_STORE_WRITE_FAILED`. Only a scrub or a rebuild repairs it.

### 6.12 Which commits are `synchronous=FULL`

`FULL` makes the commit, and every earlier one in the WAL, durable before it returns. These use it:
- clearing and setting `clean_close` (6.3 step 7, 6.12a);
- the pack roll (6.2);
- the compaction listing and switch (6.8);
- Scan's seal of the active pack (6.6).

Losing any of these to power loss could not be undone by recovery. A lost roll leaves a pack unlisted, with every later commit unreachable. A lost switch would keep the delete of the source but not the move.

Batches use `NORMAL`. Power loss can then only drop batch commits for the active pack's tail, which was `fsync`ed first and which tail recovery reads back.

## 7. Durability and crash model

**What is durable when:**
- An object is durable once its batch's pack `fsync` returns. Its commit makes it visible.
- After power loss, any batch commits not yet written to the WAL's disk copy are rebuilt from the synced tail when the store next opens.

**A crash at each step leaves:**

| Crash point | Left behind | Next open |
| --- | --- | --- |
| Before or during an entry's write | A torn or partial tail | Cut off; nothing committed is lost |
| Entry written, before the pack `fsync` | Unsynced tail | Whole entries recovered, the torn rest cut |
| After the `fsync`, before or inside the commit | A synced tail, the transaction rolled back | Recovered by tail recovery |
| After the commit, before the caller hears | Committed | Present |
| Pack file created, before its row | An unlisted pack of 64 bytes | Deleted |
| Compaction target listed or copied, before the switch | A compacting row and target file | Target deleted, source intact |
| After the switch, before the source is deleted | A retired row and the source file | Source deleted |
| Index rebuild at open, before the move | `objects.sqlite3.rebuild` | Deleted; old index, or rebuilt again |
| Any point while a writer is open | `clean_close` = 0 | Full index check |
| Staging append | Staged file with unproven bytes | Resume keeps only proven chunks |

The reference tests cover these by process kill (an in-process fault hook and real SIGKILL at every step), and by an index restored to its state after the last durable commit. A real power cut is not tested.

## 8. Concurrency model

- **One writer process** holds `writer.lock`. A second writer gets `OBJECT_STORE_BUSY`.
- **One writer task** in that process does every pack and index write, in request order, from a bounded queue of 64 requests. Queuing waits for room. Put verification runs on the callers, with at most half the CPUs verifying at once.
- **Readers in any process** use their own read-only SQLite connections (a bounded pool of 8). Point lookups run on the calling thread. A reader sees the index as of the last commit; nothing pending is visible.
- **Pack read handles** are cached, at most 64 by default, and leased for each read. The least recently used unleased handle is closed when the cache is full, and a handle being acquired is never evicted. Readers open packs with read+write+delete sharing, so the writer can append and compaction can delete while they read.
- **Compaction races:** a read whose pack has just been deleted looks its row up once more. On Linux and macOS, a read already in flight finishes on its open handle.
- **Maintenance** (Scan, Compact) runs as requests on the writer task, so it never overlaps a batch.

## 9. Error and state codes

**Thrown (store level):**

| Code | When |
| --- | --- |
| `OBJECT_STORE_NEWER_FORMAT` | A pack, entry or index written by a newer format. Nothing is changed |
| `OBJECT_STORE_FOREIGN_PACK` | Packs from two stores in one folder |
| `OBJECT_STORE_KIND_MISMATCH` | The folder's packs or index are of the other store kind than the opener expects. Nothing is changed |
| `OBJECT_STORE_BUSY` | Another writer holds the lock |
| `OBJECT_STORE_READ_ONLY` | A write reached a store opened read-only (unreachable through the read-only interface) |
| `OBJECT_STORE_MISSING` | A read-only open found no complete store |
| `OBJECT_STORE_INDEX_UNREADABLE` | A read-only open found an unusable index, or a damaged index was met in use. A Scan rebuilds it |
| `OBJECT_STORE_WRITE_FAILED` | A pack or index write failed; what was pending failed with it |
| `OBJECT_STORE_DISK_FULL` | As write failed, on a full volume |
| `OBJECT_STORE_CLOSED` | The store was closed (or stopped by a crash) before the operation finished |
| `OBJECT_STORE_LIMIT` | A call over a fixed bound: batch, whole read, range, staging budget |
| `OBJECT_STAGING_REJECTED` | Staged bytes failed a chunk check, or the staged file couldn't be read |

**Per-object refusals (returned, never stored):**

| Code | When |
| --- | --- |
| `OBJECT_KEY_TYPE_UNSUPPORTED` | Key type the store's kind doesn't hold (Cdn 1-6, Generated 7) |
| `OBJECT_NAMESPACE_UNSUPPORTED` | Namespace ≠ 0 |
| `OBJECT_TOO_LARGE` | Declared length 0 or over the type's limit, or over 64 MiB for a whole Put |
| `OBJECT_SIZE_MISMATCH` | Bytes not the declared length, or BLTE chunk sizes not adding up |
| `OBJECT_KEY_MISMATCH` | The bytes' MD5 isn't the key |
| `OBJECT_BLTE_INVALID` | Not BLTE, a bad header length or chunk table, a chunk MD5 mismatch |
| `OBJECT_BLTE_UNSUPPORTED` | A chunk table format other than 0x0F |
| `OBJECT_INDEX_INVALID` | Bad index length, footer checksum, geometry, TOC or page hash |
| `OBJECT_INDEX_UNSUPPORTED` | Another index version, page size or field widths for the key type |
| `OBJECT_HISTORY_INVALID` | A history record that doesn't follow format 1 |
| `OBJECT_HISTORY_DOCUMENT_MISSING` | A record whose document isn't stored |
| `OBJECT_DELETE_REFUSED` | Delete of a history entry |

**Read states:**
- Present: the bytes passed every check.
- Absent: no committed row.
- Corrupt: a row exists, but the bytes are truncated or fail a check, or the row is damaged.
- Lost: a row exists, but its pack file is gone.

**Put outcomes:** Stored, AlreadyPresent, Refused (with a code), or Canceled (canceled before it was appended).

**Delete outcomes:** Deleted, Absent, or Refused.

## 10. Interfaces

The calls are split by role. Each lists its inputs, outputs and guarantees.

**IObjectReader**, implemented by the store, the read-only store and the layered reader:
- **Has(key)** gives true when a committed row exists. It reads no bytes.
- **Read(key)** gives a state and, only for Present, the payload and its facts: length, origin, stored time. Objects over 64 MiB are a limit error.
- **ReadRange(key, offset, buffer)** fills the buffer from `offset` and gives a state. It reads only the blocks under the range. A range past the end, or a negative offset, is a caller error.

**IObjectWriter**, implemented by the writer store only:
- **Put(batch, commitNow)** gives one outcome per object. It returns when every stored object is committed.
- **Delete(keys)** gives one outcome per key.
- **Stage(key, declared length, origin)** gives a staging with Length, Append, Commit (an outcome), Abandon and Dispose (6.9).

**IHistoryReader**, implemented by the store and the read-only store:
- **ReadHistory(query)** gives a page of entries: kind, product, region, observed time, URL, document key and record key (6.11).

**IHistoryLog**, an IHistoryReader plus:
- **AppendHistory(observation)** takes the kind, product, region, observed time, URL and raw reply. It gives the outcomes of the document and the record.

**IObjectStoreMaintenance**, implemented by the writer store only:
- **Scan()** gives counts of packs, entries, live rows, duplicates, tombstones, corrupt and unverifiable entries, and problems.
- **Verify(full)** gives counts of checked, corrupt and lost objects, with keys.
- **Compact(fraction, max packs)** gives the packs compacted and skipped, the bytes before and after, and the rows moved.
- **Scrub(full)** gives the index problem found by the full check (or none), whether the index was rebuilt, and Verify's report (6.13).

**IObjectVerifier**: one instance, the same one Put and staging use.
- **Check(key, bytes, declared length)** gives nothing, or the refusal (code and message) that Put would give for those bytes, in the order of section 4. The one exception is `OBJECT_HISTORY_DOCUMENT_MISSING`, which depends on the store.
- **Begin(key, declared length)** gives an incremental check for an object up to its type's limit:
  - **Append(bytes)** gives a refusal as soon as the bytes can't match; once refused, it stays refused;
  - **Complete()** gives nothing or a refusal;
  - **ProvenLength** is the bytes proven so far.

## 11. Limits

| Bound | Value |
| --- | --- |
| Payload of one entry | 512 MiB (536,870,912 bytes) |
| Config / HistoryDocument / indexes / HistoryRecord | 16 MiB / 16 MiB / 64 MiB / 1,136 bytes |
| Generated | 64 MiB |
| Whole Put object, whole read, range read | 64 MiB |
| Put batch | 4,096 objects and 256 MiB |
| Delete batch | 4,096 keys |
| Pack size | 2 GiB by default; an entry larger than that goes alone into a fresh pack |
| Batch close | 128 objects, 16 MiB, 250 ms, or an empty queue |
| Writer queue | 64 requests |
| Read connections / pack handles | 8 / 64 |
| Session SHA-256 check | Objects up to 1 MiB; set of 1,048,576 entries |
| Read-day queue | 65,536 keys |
| Staging | 16 at once, 2 GiB declared in all, files kept 7 days |
| History page | 1 to 10,000 entries |
| BLTE chunks | 1 to 65,536 |
| Pack numbers | 1 to 999,999; a store that would need more refuses with `OBJECT_STORE_LIMIT` |

## 12. Worked example

One Put of a 21-byte config into a new store, with the clock fixed at 2026-10-31T00:00:00Z (Unix ms 1,793,404,800,000 = 0x000001A18F418C00):
- **Bytes:** the ASCII text `build-name = example` and a newline.
- **Key:** MD5 = `0ed94175af8022f25c2104eca1211bc6`.
- **SHA-256:** `99e4c2425650c17f101a8cf51526a45a6514410e4be60779475446c511519ccc`.

`packs/pack-000001.vmp` is 185 bytes. The store id (bytes 0x10-0x1F) is random, so it differs in every store:

```
00000000  56 4d 50 4b 01 00 40 00 01 00 00 00 00 00 00 00   "VMPK", version 1, header 64, pack 1, kind 0 (Cdn)
00000010  84 81 e6 d6 5d 09 61 c8 d1 de dc 35 a8 4e 5b 91   store id (random)
00000020  00 8c 41 8f a1 01 00 00 00 00 00 00 00 00 00 00   created (Unix ms, LE), reserved
00000030  00 00 00 00 00 00 00 00 00 00 00 00 97 02 14 c5   reserved, CRC-32C of bytes 0-59 (depends on the store id)
```

The entry starts at offset 64 (0x40):

```
00000040  56 4d 4f 45 01 01 00 00 0e d9 41 75 af 80 22 f2   "VMOE", format 1, type 1 (Config), namespace 0, flags 0, key…
00000050  5c 21 04 ec a1 21 1b c6 15 00 00 00 00 00 00 00   …key, payload length 21 (LE)
00000060  99 e4 c2 42 56 50 c1 7f 10 1a 8c f5 15 26 a4 5a   SHA-256 of the payload…
00000070  65 14 41 0e 4b e6 07 79 47 54 46 c5 11 51 9c cc   …SHA-256
00000080  00 8c 41 8f a1 01 00 00 01 10 00 00 00 00 00 00   stored at (Unix ms), origin 1 (CDN), block shift 16, reserved
00000090  01 00 00 00 00 00 00 00 00 00 00 00 cb 7b 1d 6d   sequence 1, reserved, CRC-32C of bytes 0-91 = 0x6D1D7BCB
000000a0  62 75 69 6c 64 2d 6e 61 6d 65 20 3d 20 65 78 61   payload "build-name = exa…
000000b0  6d 70 6c 65 0a 73 a2 d0 a8                        …mple\n", block table: CRC-32C of the one block = 0xA8D0A273
```

Entry size: 96 + 21 + 4 × 1 = 121 bytes, so the pack ends at 64 + 121 = 185.

The index after the commit:

| Row | Values |
| --- | --- |
| `objects` | `k` = `01 00 0e d9 41 75 af 80 22 f2 5c 21 04 ec a1 21 1b c6`, `pack` 1, `offset` 64, `length` 21, `sha256` as above, `last_read` NULL |
| `packs` | `seq` 1, `state` 0 (active), `committed_length` 185, `live_bytes` 121, `row_check` 2294590932 (0x88C4ADD4 = `CRC(i64 1 ‖ i64 0 ‖ i64 185)`) |
| `store` | `next_seq` 2, `object_count` 1, `object_digest` 8803469163995718811 (0x7A2C348FD711E49B, the one row's `Mix`), `history_count` 0, `history_digest` 0, `clean_close` 1 after the close |

The CRC values and the digest above were recomputed from this specification alone, with an independent bitwise CRC-32C (reflected polynomial 0x82F63B78) and the `Mix` steps of 5.3.
