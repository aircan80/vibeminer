# Discovery protocol version 1

Implementation contract for build discovery. Electron main launches the C# host and exchanges UTF-8 newline-delimited JSON on stdin/stdout. Logs use stderr. Both sides bound frames at 1 MiB, pending requests at 32, and public pages at 100 builds (default 50). Protocol version is the integer `1`.

## Requests and responses

Request: `{ "protocolVersion": 1, "id": "unique-string", "method": "products.list", "params": {} }`.

Success: `{ "protocolVersion": 1, "id": "unique-string", "result": {} }`.

Failure: `{ "protocolVersion": 1, "id": "unique-string", "error": { "code": "CANCELED", "message": "Discovery canceled.", "retryable": true } }`.

Progress: `{ "protocolVersion": 1, "type": "progress", "requestId": "unique-string", "stage": "Downloading metadata", "sequence": 1, "region": null, "completed": null, "total": null, "unit": null }`.

Workspace preparation also supplies `step: { index: 1, total: 22, description: "Verify the small build and CDN configurations that identify this exact build and its metadata sources." }`. The renderer prefixes the stage with `[index/total]` and displays its explanation below. Step numbers describe the actual ordered work plan, not equal durations or an overall percentage. Per-stage byte/entry counters remain part of the stage label.

`step` is optional/nullable for compatibility and unrelated discovery progress. Within a step, index is a positive safe integer; total is null until configuration verification establishes the actual plan, then a positive safe integer at least index. Unknown totals display `[1/…]`. Fresh preparation omits undeclared optional download/size families; retained ready catalogs use a shorter revalidation plan. Supporting verification/ordering messages retain their parent step. Terminal and stopping UI must not present an old active step as ongoing work.

Exactly one success/error reply completes a request. Progress includes a positive monotonically increasing `sequence` per request, is bounded/coalesced, and stops at termination. A cancel acknowledgement is distinct from the target request's terminal reply, which follows actual retirement. Reject incompatible versions, invalid requests, duplicate active IDs, oversized frames and excessive concurrent requests without crashing the process. No auto-replay of mutations after engine failure.

| Method | Parameters | Result |
| --- | --- | --- |
| `hello` | `{}` | `{ protocolVersion: 1, engineVersion: string, capabilities: string[] }` |
| `products.list` | `{}` | `{ products: Product[] }`; local reviewed registry, no HTTP |
| `discovery.read` | `{ productId, filter?: "all" \| "local" \| "advertised", limit?: number, cursor?: string }` | `DiscoveryPage`; local only |
| `discovery.refresh` | `{ productId }` | `DiscoveryPage` with default first All page after current attempt |
| `discovery.readAll` | `{ productId?, filter?: "all" \| "local" \| "advertised", limit?: number, cursor?: string }` | `DiscoveryCatalogPage` across every registered product; `productId` narrows it to one; local only |
| `discovery.refreshAll` | `{}` | `DiscoveryCatalogPage`: first All page across every product after checking every product ([D062](../docs/DECISIONS.md)) |
| `request.cancel` | `{ requestId }` | `{ requested: boolean }` |
| `shutdown` | `{}` | `{ stopping: true }`; retire work and exit |

## Projections

TypeScript shapes below document wire fields; canonical JSON schemas and shared fixtures will verify C#/TypeScript compatibility. Dates are UTC ISO 8601 strings. IDs/configs/build numbers stay strings.

```ts
type Product = { id: string; label: string; description: string };
type SourceReceipt = {
  region: string; role: "versions" | "cdns"; url: string;
  retrievedAt: string; sha256: string; byteLength: number;
  rowOrdinal: number | null;
};
type CdnConfigObservation = {
  cdnConfig: string; firstSeenAt: string; lastSeenAt: string;
  regions: string[]; advertisedRegions: string[];
  sourceReceipts: SourceReceipt[]; // newest versions-row receipt per region
};
type Build = {
  id: string; productId: string; version: string; buildId: string;
  buildConfig: string; cdnConfig: string; productConfig: string | null;
  identityFields: Record<string, string>; // never contains Region or CDNConfig
  regions: string[]; advertisedRegions: string[]; routeRegions: string[];
  firstSeenAt: string; lastSeenAt: string;
  isNew: boolean; isAdvertised: boolean; isLocal: boolean;
  availability: "unknown";
  sourceReceipts: SourceReceipt[];
  cdnConfigHistory: CdnConfigObservation[]; // newest first, at most 16
  cdnConfigHistoryCount: number; // total; older entries beyond the cap are omitted, not lost
};
type DiscoveryProblem = {
  region: string | null; code: string; message: string;
  severity: "problem" | "note"; // a note never makes a check partial (D076)
};
type DiscoveryPage = {
  productId: string; builds: Build[]; nextCursor: string | null;
  lastCheckedAt: string | null; checkedRegions: string[];
  errors: DiscoveryProblem[]; hasSavedHistory: boolean;
  connection: "notChecked" | "online" | "offline" | "partial";
};
```

In a discovery page `cdnConfig` is the newest entry of `cdnConfigHistory`. In a workspace snapshot it is the CDN config that open or admission was made with. Whether switching would change anything is decided per source region: only when the open's own region now advertises a different CDN config (its `advertisedRegions`, else its newest `sourceReceipts` for that region) is a new Open worth it. Entries with the same first and last check are a same-check tie ordered only by key; that order means nothing. The projection holds 1 to 16 entries, the newest first (schema `minItems`/`maxItems`); `cdnConfigHistoryCount` is the total. A refresh updates Discovery's projection of an open build, and the Home build header uses it for the same per-region check ([D073](../docs/DECISIONS.md)).

### Across products

The desktop lists every product's builds in one grid ([D062](../docs/DECISIONS.md)). It uses only `discovery.readAll` and `discovery.refreshAll`; the single-product methods keep their behaviour.

```ts
type ProductDiscoveryStatus = {
  productId: string; lastCheckedAt: string | null; checkedRegions: string[];
  errors: DiscoveryProblem[]; hasSavedHistory: boolean;
  connection: "notChecked" | "online" | "offline" | "partial";
};
type ProductRefreshProblem = { productId: string; code: string; message: string };
type DiscoveryCatalogPage = {
  productId: string | null; // the Product filter; null = every product
  filter: "all" | "local" | "advertised";
  builds: Build[]; nextCursor: string | null; // at most 100 builds
  products: ProductDiscoveryStatus[]; // every registered product in registry order, whatever the filter
  refreshProblems: ProductRefreshProblem[]; // always [] from discovery.readAll
};
```

- **Order.** Registry product order, then the raw `BuildId` read as a decimal number, newest first (missing, non-decimal or longer than 18 digits sort last), then build identity. The order is only for display; identity is unchanged. Each `Build` is the same exact projection `discovery.read` returns, with the same integrity checks.
- **Paging.** The cursor binds scope, product filter, visibility filter and the number of committed checks. A commit of any product refuses an older cursor with `STALE_CURSOR`; starting a refresh does not. A page carries at most 750,000 bytes of builds and at most 900,000 bytes of builds plus statuses, inside the 1 MiB frame.
- **Per-product status.** `products[i]` is that product's saved latest check: the same `lastCheckedAt`, `checkedRegions`, `errors`, `hasSavedHistory` and `connection` a single-product page carries. A product never checked is `notChecked`.
- **Problems and notes.** `errors` holds every finding of the latest check. `severity: "problem"` means the check did not get something it asked for; any problem makes a check that read the versions document `partial`. `severity: "note"` is a regional fact about the official source and leaves the check `online`. Today the only note is `ROUTE_MISSING`: a region the versions document advertises with no `cdns` row at all (Blizzard publishes `sg` and `xx` this way for `wow`, `wowt` and `wow_beta`). Its builds are kept, and it is never a download source. `ROUTE_AMBIGUOUS`, `ROUTE_INVALID` and `ROUTE_UNAVAILABLE` stay problems. The desktop counts, retries and calls a refresh incomplete only for problems; it shows notes beside the regions they describe. Checks saved before D076 carry no severity in the catalog: reads report their `ROUTE_MISSING` as a note and a check whose only findings were notes as `online`.
- **Completed or failed.** A latest check received evidence when `connection` is `online`, or `partial` with at least one `checkedRegions` entry (the versions document was read). `offline`, and `partial` with no checked regions (for example HTTP 500 on both documents), are failed attempts: the desktop names them as "could not be checked", never as a check that found nothing, and never counts them or shows them as the latest check.
- **Refresh.** `discovery.refreshAll` runs the existing per-product refresh for every registered product, at most three products at once, through the same official-source transport and its network and HTTP permits. Each product commits on its own. A product whose attempt fails with a typed error keeps its earlier evidence and appears in `refreshProblems`; the others continue. Network failures are ordinary committed checks with `offline` or `partial` status, as before.
- **Progress.** One frame before any product starts, then one per finished product, in order: `{ stage: "Checking official metadata", completed, total, unit: "products", region: null }`. `total` is the registry size. There is no percentage, byte count or ETA.
- **Cancel.** `request.cancel` stops starting products and cancels the running ones. The terminal `CANCELED` reply follows only after the running checks retired. Products that committed before the stop keep their new results, so the desktop reads the saved view again. If every product had already finished, the durable result wins and the reply is a success.

`connection` describes the latest explicit attempt, not a general connectivity probe. Local reads make no HTTP calls. An engine/network failure cannot masquerade as successful empty results. `isLocal` is false for discovery-only observations; Local can be empty until actual preparation exists. Render availability as Not checked, never infer payload readiness or locale.

## Discovery source and identity policy

Use the fixed official metadata origin `https://us.version.battle.net`, requesting one `/v2/products/{productId}/versions` and `/cdns` pair per refresh. Each pair already contains regional rows; do not fan out the same URLs once per region. `products.list` uses the reviewed exact WoW registry; no implicit remote summary check on startup. Preserve observed rows for all bounded valid advertised regions; matching CDN rows establish route evidence. A missing route is a regional note and an ambiguous or unusable one a regional problem; neither erases the advertisement ([D076](../docs/DECISIONS.md)). Never substitute US routing.

Group identity includes exact product and Published channel, version, raw BuildId, normalized BuildConfig/ProductConfig hashes, and all additional versions fields including KeyRing/unknown values. Exclude Region, route and CDNConfig ([D061](../docs/DECISIONS.md)). CDNConfig is required and kept per observation and in the build's CDN config history. Preserve original documents, exact fields and row ordinals with immutable event receipts. Use canonical length-framed/sorted identity serialization with a revision; a digest is not permission to discard full identity data.

Maintain successful regional baselines and a product-visit marker. First successful check of a region is neutral; later new identities can be New to VibeMiner. Repeated discovery never acknowledges Open. Failed/canceled scopes preserve prior successful advertisement evidence. Use persisted monotonic check generations rather than wall clocks to fence stale attempts and paginate a stable snapshot. Cursors bind product/filter/generation and refuse stale mismatches rather than mix pages.

Input limits: 1 MiB/document, 64 columns, 10,000 data rows, 16,384 characters/line, 64 regions. Validate strict UTF-8, field descriptors, duplicate names, row arity, NUL, and required identities. Retain unknown fields and source syntax. Standard .NET primitives only; no game parser/download package.

## Host and data lifecycle

Host accepts optional `--data-dir <absolute-path>` for isolated tests/development. Electron supplies its app-owned `userData/engine-v1` location; never open the Java archive by default. Tests use temporary directories. Normal shutdown cancels and awaits active request work before closing the catalog. Unexpected exit rejects all pending frontend consumers. Transport/recovery errors are safe stable codes rather than exception-string parsing.

The UI preserves the current filters, selection and saved rows during refresh, supports explicit stopping/cancellation, and fences late results by request generation. Refresh preserves a valid selection. Discovery queries do not initialize payload acquisition or mark metadata ready. Open Build stays disabled with a visible reason until real opening is implemented.
