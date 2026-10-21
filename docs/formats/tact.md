# TACT: configs, BLTE, manifests and CDN fetching

What our original TACT readers accept, what they refuse, and what each source does and does not prove. Code lives in [`src/backend/VibeMiner.Protocols.Tact/`](../../src/backend/VibeMiner.Protocols.Tact/). CDN transport and locators live in [`src/backend/VibeMiner.Infrastructure/Content/`](../../src/backend/VibeMiner.Infrastructure/Content/) and [`Acquisition/`](../../src/backend/VibeMiner.Infrastructure/Acquisition/). WoW ROOT is in [root.md](root.md).

All limits below are the code defaults. A legitimate input above a limit gets an explicit `*_LIMIT` error, never silent truncation.

## Common reader rules

- Readers take caller-owned decoded streams or spans. They never fetch, open paths, touch SQL or schedule work. Streams stay open on success and failure.
- Streaming readers await every consumer callback before reading more (backpressure, no queue). Every emitted row is provisional until the reader reaches exact EOF **and** the caller's separate BLTE/source proof succeeds. A late failure means nothing is published.
- Multi-byte integers in EN, IN, DL, DS, BLTE headers and archive-index rows are big-endian. ROOT and archive-index footer counts are little-endian.
- Unknown signatures, versions or layouts return `*_UNSUPPORTED`. We never fall back to a guessed legacy layout.
- Errors use `DiscoveryException` codes with prefixes `CONFIG_`, `BLTE_`, `EN_`, `IN_`, `DL_`, `DS_`, `ARCHIVE_INDEX_`. Cancellation stays `OperationCanceledException`; stream I/O exceptions propagate unchanged.

## Discovery documents (versions, cdns)

Fetched from `https://us.version.battle.net/v2/products/{product}/{versions|cdns}` with a 20 s timeout, no redirects, media type `text/plain` or `application/octet-stream`.

[`MetadataParser`](../../src/backend/VibeMiner.Protocols.Tact/MetadataParser.cs) grammar:

- Header row of `Name!TYPE:width` descriptors separated by `|`. Names must be unique.
- `## seqn = N` comments carry the sequence number; two different values are an error. Other `#` lines are comments.
- Strict UTF-8, LF or CRLF only, no NUL, every row has exactly as many cells as the header.
- Limits: 1 MiB, 64 columns, 10,000 rows, 16,384 characters per line.

A CDN route is `https://{host}/{Path}/` from the matching `cdns` row. Extra mirrors come from `Servers`; see [Routes](#routes).

## Configuration files

Build config and CDN config are fetched from `{route}config/{key[0..2]}/{key[2..4]}/{key}`:

- One GET, 20 s timeout, no redirects, at most 1 MiB, nonempty, body length must equal `Content-Length` when present.
- Media types `text/plain`, `application/octet-stream` and `binary/octet-stream` are accepted. The official CDN serves configs as `binary/octet-stream`. Anything else (for example `text/html`) is `CONFIG_CONTENT_TYPE`.
- The MD5 of the raw bytes must equal the advertised key. That check belongs to the acquirer, not the parser.

[`TactConfigurationParser`](../../src/backend/VibeMiner.Protocols.Tact/TactConfigurationParser.cs) grammar:

| Rule | Detail |
| --- | --- |
| Encoding | Strict UTF-8. BOM anywhere is rejected. |
| Lines | LF or CRLF. A lone CR is rejected. |
| Controls | Any control character except tab (including NUL) is rejected. |
| Comments | Whole lines starting with `#` after trimming. |
| Assignment | `name = token token ...` with exactly one `=`. Tokens split on spaces and tabs; empty values are allowed. |
| Names | Lowercase `[a-z0-9]` first character, then `[a-z0-9-]`. |
| Duplicates | A repeated name is rejected. |
| Unknown names | Kept as-is with token order. |
| Limits | 1 MiB, 16,384 lines, 65,536 characters per line, 4,096 fields, 4,096 tokens per field. |

### Effects build-config budget

The optional item-effects path runs a stricter preflight before calling the parser ([`ItemEffectsConfigurationBudget`](../../src/backend/VibeMiner.Infrastructure/ItemEffects/ItemEffectsConfigurationBudget.cs)): 128 KiB, 2,048 `=` characters, 8,192 tokens, 16,384 space/tab separators. Comments are counted too. The separator cap exists because `string.Split` must walk every repeated separator even when it starts no new token, so long whitespace runs cost scratch work without adding tokens.

### Metadata declarations

A role is declared as `role = CKey [EKey]` with an optional `role-size = decodedSize encodedSize`. Note the order: **decoded first, encoded second**. Keys are 32 hex characters, not all zero. Lengths are positive canonical decimals.

| Role | Fields | Meaning |
| --- | --- | --- |
| EN | `encoding`, `encoding-size` | Must be direct: both keys and both sizes. EN is the bootstrap for everything else. |
| ROOT | `root` | Exactly one nonzero CKey. Resolved to an EKey and sizes through EN. A `root-size` companion is refused. |
| IN | `install`, `install-size` | Direct pair or CKey-only. |
| DL | `download`, `download-size` | `download-size` is the size pair of **the DL manifest object itself**. It is not a game download total. |
| DS | `size`, `size-size` | The download-size manifest. Separate from `download-size`. |

The ROOT-mode declaration policy is `wow-root-metadata-declarations-v1` ([`MetadataDeclarations`](../../src/backend/VibeMiner.Infrastructure/Inventory/MetadataDeclarations.cs)):

| Situation | Rule |
| --- | --- |
| VFS fields (`vfs-root`, `vfs-N`, their `-size`) beside a valid ROOT | Preserved as an advertised alternative. Never fetched. |
| ROOT absent or zero, so the build needs TVFS | Unsupported. No silent fallback. |
| `patch`, `patch-size`, `patch-index`, `patch-index-size` | Classified as outside the full-object closure. Not fetched, not applied, not claimed absent. |
| Informational fields (`build-name`, `build-uid`, `build-product`, `build-branch`, `build-number`, `build-attributes`, `client-version`, `feature-placeholder`, `build-partial-priority`, `product`, `uid`) | Kept opaque. |
| Unknown reference-like field: a name segment `manifest`/`root`/`index`, a field with a `-size` companion, or a 32-hex value | Refuses a completeness claim (`METADATA_UNSUPPORTED`). |
| `role-size` without `role` | `METADATA_INVALID` (orphan). |
| EKey given but no sizes | `METADATA_INVALID`. |
| Optional DL/DS | Counts as not published only when the authenticated config does not declare it. A 404, a fetch failure, a parse failure, an empty or malformed declaration is **not** absence. |
| Declared sizes above the BLTE limits | `METADATA_LIMIT`. |

The DL-scoped download path ([`DownloadDeclarations`](../../src/backend/VibeMiner.Infrastructure/Downloads/DownloadDeclarations.cs)) only needs `download` and a direct `encoding`; it does not require ROOT or IN.

## BLTE

Wire layout:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `BLTE` |
| 4 | 4 | Header length, BE u32. `0` means headerless: one chunk covering the rest of the object. |
| 8 | 1 | Table format. Only `0x0F` is admitted. |
| 9 | 3 | Chunk count, BE u24. Must be nonzero. |
| 12 | 24 × n | Per chunk: BE u32 encoded size, BE u32 decoded size, 16-byte MD5 of the encoded chunk. |

The header length must equal `12 + 24 × count` exactly. Each chunk starts with a mode byte that is part of its encoded size and its MD5.

Identity rules:

- **Chunked:** the EKey is the MD5 of the header (bytes `0 .. headerLength`). Each chunk has its own MD5.
- **Headerless:** the EKey is the MD5 of the whole encoded object, including the 8-byte prefix.
- **CKey** is the MD5 of the complete decoded content.
- Neither key is a local storage hash. Local CAS objects are named by SHA-256.

Modes: `N` (raw) and `Z` (zlib) decode. `E` (encrypted) is a distinct error, `BLTE_ENCRYPTED`, never zero-filled. Other modes are `BLTE_UNSUPPORTED`.

Zlib must end exactly at the chunk boundary. `ZLibStream` reads ahead and accepts truncated input at EOF, so the decoder withholds the last compressed byte: a valid member must ask for it to finish its checksum, and a member with trailing data finishes first. Keep the regression tests for this when changing runtimes.

### Limits

| Limit | Value |
| --- | --- |
| Encoded object | 512 MiB |
| Decoded object | 1 GiB |
| Chunks | 65,536 |
| One compressed `Z` chunk (buffered whole) | 16 MiB |
| Read/output slice | 64 KiB |

Raw chunks stream. Only one compressed chunk is buffered at a time. The decoder never allocates the whole decoded object.

### Three proofs

| Revision | Code | Proves | Use |
| --- | --- | --- | --- |
| `content-blte-v1` | [`BlteDecoder.DecodeAsync`](../../src/backend/VibeMiner.Protocols.Tact/BlteDecoder.cs) | EKey scope, every chunk MD5, exact totals and EOF, **full decoded CKey**. N/Z only. | Normal content and metadata. |
| `content-blte-projection-v1` (policy `blte-clear-chunks-v1`) | [`BlteDecoder.DecodeAvailableAsync`](../../src/backend/VibeMiner.Protocols.Tact/BlteProjectionDecoder.cs) | Same encoded proof, decoded readable spans with SHA-256 each, exact hole geometry. CKey only when there are no holes. | Tables with encrypted chunks. |
| `blte-encoded-v1` | [`BlteEncodedVerifier`](../../src/backend/VibeMiner.Protocols.Tact/BlteEncodedVerifier.cs) | Encoded identity only: header/whole-object MD5, every chunk MD5, positive chunk sizes, exact EOF. Modes are opaque. No CKey. Headerless decoded length is unknown. | Bulk retention of encoded objects. |

These proofs are not interchangeable. A projection or an encoded-only proof never satisfies a `content-blte-v1` requirement.

### Encrypted chunks and projections

`E` chunk envelope, as admitted for projections:

| Offset in chunk | Size | Field |
| --- | --- | --- |
| 0 | 1 | `E` |
| 1 | 1 | Key-name length, must be 8 |
| 2 | 8 | Key name |
| 10 | 1 | IV length, must be 4 |
| 11 | 4 | IV |
| 15 | 1 | Cipher, must be `S` |
| 16.. | | Ciphertext |

**Key names:** read the 8 wire bytes as a little-endian u64 and show it as 16 uppercase hex digits. Wire bytes `0c2d177629cc431c` are key `1C43CC2976172D0C`. This matches WDC5 section keys and the public key-list convention. It is not raw wire-order hex.

Projection rules:

- Headerless encrypted objects are unsupported: there is no header to anchor a partial proof.
- Every chunk MD5 is checked, encrypted ones included. An encrypted chunk must declare a positive decoded span.
- Holes keep their original chunk ordinal and key name. They are never merged or renumbered.
- Up to 128 input chunks: one readable range per clear chunk. Above 128: adjacent clear chunks coalesce into one readable range, hashed incrementally. Up to 65,536 input chunks are accepted; readable plus unavailable output ranges must stay at most 128.
- Each chunk, encoded and decoded, is at most 16 MiB.
- [`BlteProjectionStream`](../../src/backend/VibeMiner.Protocols.Tact/BlteProjectionStream.cs) exposes the declared decoded length. Seeking over holes is fine; any read touching a hole fails with `BLTE_RANGE_UNAVAILABLE` before touching backing bytes. Holes are never zero bytes.

No decryption is implemented. A public key list was checked for coverage only: it lacked some of the key names one Retail Item file needed, so decryption alone cannot promise full tables.

## EN (encoding) v1

[`EncodingInventoryReader`](../../src/backend/VibeMiner.Protocols.Tact/EncodingInventoryReader.cs) enumerates everything. [`EncodingFileLookup`](../../src/backend/VibeMiner.Protocols.Tact/EncodingFileLookup.cs) validates the whole file once, then answers CKey and EKey lookups by reading single pages through the page index. Neither builds an in-memory map of the whole file.

Header, 22 bytes, big-endian:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | `EN` |
| 2 | 1 | Version, must be 1 |
| 3 | 1 | CKey width, must be 16 |
| 4 | 1 | EKey width, must be 16 |
| 5 | 2 | Content page size in KiB, nonzero |
| 7 | 2 | Encoding page size in KiB, nonzero |
| 9 | 4 | Content page count |
| 13 | 4 | Encoding page count |
| 17 | 1 | Must be 0 |
| 18 | 4 | ESpec block size |

Then, in order:

1. ESpec block: NUL-terminated printable-ASCII strings. Must end with NUL.
2. Content page index: 32 bytes per page (first CKey, MD5 of the page). Then the content pages.
3. Encoding page index: same shape. Then the encoding pages.
4. Trailer: printable ASCII to EOF, at most 1 MiB.

Content row: u8 candidate count (1..255), u40 decoded size, CKey, then `count` EKeys in order. `22 + 16 × count` bytes, wholly inside one page. A count of 0 starts zero padding.

Encoding row: EKey, u32 ESpec index (must exist), u40 encoded size. 25 bytes, wholly inside one page. A zero key starts padding. Only the last encoding page may carry the final marker: zero key followed by `0xFFFFFFFF`.

Validation: page MD5 before any row is emitted; index first keys nonzero and strictly increasing; each page's first row equals its index key; rows strictly ordered and below the next page's first key; nonzero candidate keys; zero padding; every page has at least one row.

EN has no reverse index. An EKey row does not name its CKey, and a CKey may list several EKeys. Do not assume a one-to-one association.

Limits: 1 GiB decoded, 50 M rows, 100 M candidate references, 1 M pages, 32 MiB per page index, 64 KiB per page, 65,536 rows per page, 1 M ESpec strings, 32 MiB ESpec, 1 MiB trailer.

## IN, DL and DS

Shared tag format: NUL-terminated strict UTF-8 name, BE u16 raw type, then a membership bitmap of `ceil(entries / 8)` bytes, MSB first (bit 7 of byte 0 is entry 0). Unused padding bits are preserved. Names are data, never filesystem paths. IN tag names must be nonempty; DL and DS tag names may be empty.

| Manifest | Header | Order | Entry |
| --- | --- | --- | --- |
| IN v1 | `IN`, version 1, key width 16, u16 tags, u32 entries (10 bytes) | tags, then entries | nonempty name, CKey, u32 **decoded** size |
| DL v1 | `DL`, version, key width 16, u8 checksum-present, u32 entries, u16 tags (11 bytes) | entries, then tags | EKey, u40 **encoded** size, u8 priority, [u32 checksum], [flag bytes] |
| DL v2 | v1 + u8 flag-byte count | same | same |
| DL v3 | v2 + u8 base priority + 3 unknown bytes | same | same |
| DS v1 | `DS`, version 1, key width 9 or 16, u32 entries, u16 tags, u40 declared total (15 bytes) | tags, then entries | key (9 or 16 bytes), u32 encoded size |

Rules:

- DL entries are not in EKey order, and no ordering is guaranteed. Deduplicating needs a sort or external merge; comparing adjacent rows is not enough. Duplicate EKeys keep their own ordinals.
- DL checksums are opaque. Parsing them verifies nothing. Priority, base priority, flags, the checksum-present byte and the three v3 unknown bytes are kept raw. DL priority is not our scheduler priority.
- A DS 9-byte key is a **prefix**. It is never zero-padded into a full EKey and never assumed unique: the reference writer serializes only the prefix of keys that were unique at full width, so distinct objects can share a prefix.
- The DS header total must equal the checked sum of entry sizes, duplicates included.
- Exact EOF is required for all three.

Limits ([`InventoryContracts`](../../src/backend/VibeMiner.Protocols.Tact/InventoryContracts.cs)):

| | IN | DL | DS |
| --- | --- | --- | --- |
| Decoded bytes | 16 MiB | 1 GiB | 1 GiB |
| Entries | 500,000 | 10 M | 10 M |
| Tags | 4,096 | 4,096 | 4,096 |
| Name bytes | 4,096 | 16,384 | 16,384 |
| Bitmap per tag | 2 MiB | 2 MiB | 2 MiB |
| Aggregate tag work | 512 MiB | 512 MiB | 512 MiB |
| Other | 16 MiB aggregate entry names | ≤255 flag bytes | |

## What each source establishes

| Source | Establishes | Does not establish |
| --- | --- | --- |
| Build/CDN config | Exact role identities; direct keys and both lengths of declared metadata objects | Game size. `download-size` is the DL file's own size pair. Payload availability. |
| DL | Per row: full EKey and encoded size, tags, priority, flags | CKey, decoded size, ROOT membership, installed size |
| DS | Per row: 9-byte prefix or 16-byte EKey, encoded size, tags; declared total of row sizes | Prefix uniqueness; full identity for 9-byte keys; reuse from a prefix match |
| EN | CKey → decoded size and ordered EKeys; EKey → encoded size and ESpec | The published file list; a reverse EKey → CKey association |
| ROOT | FileDataID → CKey memberships per locale/content variant, optional name hashes | Sizes, filenames, a global FDID index |
| IN | Named install entries with CKey and decoded size | The ROOT catalog or installed size |

Size semantics (checked against TACT.Net `57618c47dc0cfef1bfc0e7585bf335ced7218389`: `DownloadFileEntry`, `DownloadSizeHeader`, `DownloadSizeFile`, `BlockTableStreamWriter`):

- DL and DS sizes are the length of the finished **encoded BLTE object**, not decoded bytes and not HTTP traffic.
- The DS header total is the sum of row sizes. It is not a unique-object total when prefixes repeat.
- A DL-scoped total sums distinct full EKeys once. Duplicate rows with the same size count once. Conflicting sizes for one EKey block an exact total (`DOWNLOAD_SIZE_CONFLICT`).
- Never add DL, DS, EN decoded and IN totals together. They measure different things over overlapping scopes.
- Unknown is not zero. A missing or ambiguous size leaves the total unresolved.

## Archive indexes

[`ArchiveIndexParser`](../../src/backend/VibeMiner.Protocols.Tact/Archives/ArchiveIndexParser.cs) admits only the native per-archive layout.

Footer, last 28 bytes of the `.index` file:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | TOC hash: first 8 bytes of MD5 over the TOC |
| 8 | 1 | Version, must be 1 |
| 9 | 2 | Must be 0, 0 |
| 11 | 1 | Page size in KiB, must be 4 |
| 12 | 1 | Offset width, must be 4 |
| 13 | 1 | Size width, must be 4 |
| 14 | 1 | Key width, must be 16 |
| 15 | 1 | Checksum width, must be 8 |
| 16 | 4 | Entry count, **little-endian** u32 |
| 20 | 8 | Footer checksum: first 8 bytes of MD5 over footer bytes 8..27 with these 8 bytes zeroed |

- The index file is named by the **MD5 of the 28-byte footer**, not of the whole index.
- Geometry: 170 entries per 4,096-byte page; `pages = ceil(entries / 170)`; total length `pages × 4096 + pages × 24 + 28`. The observed file length must match.
- TOC: `pages × 16` bytes of per-page maximum keys (nonzero, strictly increasing), then `pages × 8` bytes of truncated page MD5s.
- Entry, 24 bytes: EKey, BE u32 encoded size (nonzero), BE u32 offset in the archive. Keys are strictly increasing and above the previous page's maximum; the last key equals the page maximum; padding is zero.
- A TOC maximum only selects the page to check. It does not prove the key is present.
- A CDN config's `archives-index-size` value is a hint, not authentication. Java-era evidence found a 135,988-byte hint for an index whose authenticated geometry was 173,068 bytes. Use authenticated geometry.
- An index proof authenticates a location. It does not authenticate the archive bytes; the fetched object still needs its own BLTE proof.

Index offset width tells the index kind (TACTSharp `IndexInstance`):

| Width | Kind | Admitted |
| --- | --- | --- |
| 0 | Loose file-index (`file-index`): gives encoded size only, no archive | No |
| 4 | Native archive index | Yes |
| 6 | Archive-group index | No |

Group index layout, for reference only (TACTSharp `GroupIndex`): 4 KiB pages, 26-byte records of EKey, BE u32 encoded size, BE u16 archive ordinal (position in the config's `archives` list), BE u32 offset. TACTSharp builds the group locally from native indexes instead of downloading it. On a captured Retail CDN config the declared `archive-group` URL returned 404, so do not plan on a server-side group index.

Limits: 10 M entries, 100,000 pages, 512 MiB index, 4 MiB TOC.

## CDN fetching

### URLs

[`OfficialSourceTransport.ValidateUri`](../../src/backend/VibeMiner.Infrastructure/Content/OfficialSourceTransport.cs) admits only:

- `https`, default port, no user info, query or fragment;
- host `level3.blizzard.com`, `level3.ssl.blizzard.com` or `{2-8 lowercase letters}.cdn.blizzard.com`;
- path `/tpr/{product path}/data/{xx}/{yy}/{32 hex}` with optional `.index`.

Data objects and indexes live at `{route}data/{key[0..2]}/{key[2..4]}/{key}` and `…/{key}.index`.

### Routes

At most four routes per object: the selected route first, then up to three from the `cdns` row's `Servers` field. A server entry must be `https`, default port, path `/`, and may only carry `maxhosts=N` / `fallback=0|1` query parameters. Moving to the next route is allowed for unavailability, network errors, timeouts, `CONTENT_SOURCE_MISSING`, `CONTENT_LOCATOR_INCOMPLETE` and `CONTENT_NOT_LOCATED`. Redirects, integrity failures and budget failures never authorize another route.

### Loose objects

- Exactly one GET to `data/xx/yy/EKey`. No HEAD, no redirects (a redirect is `SOURCE_REDIRECT`).
- 200 only. A wrapped (`Content-Encoding`) or ranged response is refused. `Content-Length`, when present, must equal the expected encoded size.
- Media types: `text/plain`, `application/binary`, `application/octet-stream`, `binary/octet-stream`.
- 15-minute deadline per source, 30-second idle read, 64 KiB buffer.
- The shared producer fetches objects larger than 4 MiB as consecutive ranges of at most 4 MiB.

### Ranges

- Requires 206 with `Content-Range` in bytes whose start, end and length match the request exactly, an expected total when one is known, no `Content-Encoding`, total at most 16 GiB.
- 45 s per range request. Index reads are at most 4 MiB.
- A range 404 is `CONTENT_SOURCE_MISSING`: that source is unavailable on that route, not absent everywhere.

### When to search archives

Per key and per route: only an exact loose **404** authorizes an archive search on that route. When the build has a sealed bulk map that lists the key, the mapped archive locations are tried first and then loose; a listed DL member found in neither goes straight to the earlier-config fallback below (the map already covered the current archives) and is `CONTENT_NOT_LOCATED` (retryable) if that finds nothing.

### Per-key archive locator

[`ArchivedContentLocator`](../../src/backend/VibeMiner.Infrastructure/Content/ArchivedContentLocator.cs):

- Needs a CDN config `archives` list of at most 4,096 distinct lowercase keys.
- Four workers walk the archive list. Per candidate: 28-byte footer, the TOC, then at most one 4 KiB page.
- Per search: 5 minutes, 16,384 range requests, 128 MiB of requested bytes.
- An authenticated entry whose size differs from EN is `CONTENT_SIZE_CONFLICT`.
- Unavailable indexes make the result `CONTENT_LOCATOR_INCOMPLETE`, not "not present".
- A found location is saved as a proof (footer, TOC, page; at most 8 MiB) in CAS, bound to the **EKey and the CDN config SHA-256**. Reuse re-hashes the proof and replays every check, including the expected size.

### Earlier CDN configurations

A republished CDN config can drop an archive the build still needs ([D069](../DECISIONS.md)). In the shared producer, when the source's own CDN config does not locate a key, the locator also searches archives listed only by the build's earlier CDN configs:

- Earlier means after the source's CDN config in the build's newest-first history (last seen, first seen, key), at most 15 of them, so 16 with the source's own. A newer config is never used. Only the build's own discovery history authorizes them.
- Newest first. Each config's archives minus every archive already searched; at most 4,096 extra archives per lookup.
- An earlier config is fetched only when a lookup reaches it: `{route}config/xx/yy/key`, at most 1 MiB, 20 s, through the same HTTP permits and priority as the object. Raw MD5 and grammar are checked, then the bytes go to CAS and `configuration_objects` like any configuration; a saved copy is re-hashed and re-checked on every load. The parsed archive list is shared through the proof cache.
- The fallback shares the lookup's 16,384-request and 128 MiB budgets and gets its own 5-minute limit. Its indexes use the same proof cache and checks as current ones; a found proof is saved under the earlier config's SHA-256.
- An earlier config the CDN does not serve intact (404, other HTTP or network failure, wrong MD5, bad grammar, over a limit, no supported archive list) counts as unavailable, and the lookup moves on to the next one. A broken saved copy (`LOCAL_CONFIGURATION_CORRUPT` or `LOCAL_CONFIGURATION_MISSING`, including saved bytes that no longer pass MD5 or grammar), a catalog or store failure (`CATALOG_*`, `CONTENT_STORE_*`), cancellation, the lookup budget and proof-cache capacity are not: they fail the lookup and are reported, and the saved copy is never silently replaced from the CDN. That, or a 404 for an earlier index, makes the result `CONTENT_LOCATOR_INCOMPLETE`; for a sealed-map DL member it stays retryable, so a retention pass waits instead of failing. A located archive that then 404s is `CONTENT_SOURCE_MISSING`. Neither is ever recorded as corrupt. An object no searched config lists is `CONTENT_NOT_LOCATED` (absent).
- The encoded receipt's `cdnConfig` names the CDN config whose archive list authorized an archive location (the source's own or the earlier one); it is null for loose objects and older receipts.

Shared proof cache ([`ArchiveProofCache`](../../src/backend/VibeMiner.Infrastructure/Acquisition/ArchiveProofCache.cs)): 32 MiB / 4,096 retained entries, 32 pending fetches (24 for background work). Only `CONTENT_SOURCE_MISSING` is cached as a negative, for 30 seconds. Corruption, timeouts, cancellation and capacity refusals are never cached as absence.

### Bulk archive map

[`BulkArchiveLocator`](../../src/backend/VibeMiner.Infrastructure/Acquisition/BulkArchiveLocator.cs) is explicit Download-all preparation, never a prerequisite for browsing. It downloads and authenticates every native index of the build's archive list (footer probe, then ranges of at most 4 MiB), merges them against the sorted DL ledger, and seals a map of DL members to archive locations.

| Limit | Value |
| --- | --- |
| Archives | 4,096 |
| Aggregate native index bytes | 512 MiB |
| DL members / output locations | 10 M / 10 M |
| Open index files (LRU) | 32 |
| Cursor working memory | 32 MiB |
| Provenance manifest | 2 MiB |

Same-size duplicate locations are kept; conflicting sizes for one EKey fail. The map is bound to the exact CDN config SHA-256, archive list and ledger revision. Binary layout: [ARTIFACT-FORMATS.md](../ARTIFACT-FORMATS.md#bulk-archive-map-vmbarcm1).

## References

Format facts were taken from these pinned sources. Our code is original; nothing was copied.

- Original Java VibeMiner checkpoint `8f353d046d961108f7acc86bfe1381004467afd5` (`TactConfigParser`, `BlteDecoder`, `FullEncodingInventory`, `InstallManifestParser`, `DownloadInventoryReader`, `DownloadSizeInventoryReader`, `ArchiveIndexParser`, `BuildMetadataDeclarationClassifier`).
- TACTSharp `67fd53ac852ca297e9bf95bb41d1a97da202eba4` (MIT): `EncodingInstance`, `Build`, `BLTE`, `IndexInstance`, `GroupIndex`.
- TACT.Net `57618c47dc0cfef1bfc0e7585bf335ced7218389`: DL/DS headers, entries, writer and BLTE writer size semantics.
- CascLib `38a34665624b8775bb875274b36191b21c38d97b`: reference only.
