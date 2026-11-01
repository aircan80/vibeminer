# Build catalog v1

Capability `build-catalog-v1` adds `catalog.list` and `catalog.refresh` ([#205](https://github.com/aircan80/vibeminer/issues/205)): the Discovery build list read from the layer 3 catalog over the object store, the same data `vm browse` shows. The Host advertises it in `hello` only when it is composed with that catalog. Without it the desktop shows the old Discovery grid; the `discovery.*` methods stay as they are until #158. Schemas: `BuildCatalog`, `CatalogProduct`, `CatalogBuild`, `CatalogProblem`, `CatalogSummary`, `BuildCatalogRefresh`, `CatalogRefreshFailure`.

| Method | Parameters | Result |
| --- | --- | --- |
| `catalog.list` | `{}` | `BuildCatalog`; local only, never touches the network |
| `catalog.refresh` | `{}`, with a request id | `BuildCatalogRefresh`; asks the version service, cancellable, sends progress |

Any other parameter is `INVALID_REQUEST`.

## Shapes

```ts
type CatalogBuild = {
  id: string;              // build identity v2, 1-128 chars
  versionsName: string;    // 1-128 chars
  buildConfig: string;     // 32 lower hex
  current: boolean;
  regions: string[];       // distinct, first-listed order; for a current build only the regions whose listing is current. At most 64
  firstSeenAt: string; lastSeenAt: string;
};
type CatalogProblem = { code: string; message: string };   // code [A-Z0-9_]{1,64}, message 1-512 chars
type CatalogProduct = {
  productId: string; label: string;  // reviewed id and label (wowxptr is "PTR 2"); an unreviewed product's label is its id
  reviewed: boolean;                 // false: a WoW product the summary lists that isn't reviewed (D476)
  builds: CatalogBuild[];            // newest first sighting first, at most 100
  buildCount: number;                // every build the catalog has for the product, >= builds.length
  lastObservedAt: string | null;     // newest versions reply; null = no versions history
  problems: CatalogProblem[];        // the first 20
  problemCount: number;              // all of them
};
type CatalogSummary = {
  observedAt: string;
  readable: boolean;                 // false: the stored summary can't be read, so new products are unknown
  newProducts: string[];             // WoW products the summary lists that aren't reviewed, sorted, at most 64; each is also a product
  withoutVersions: string[];         // WoW products it lists only with flagged rows (cdn ...): no versions document, so no history; at most 100
};
type BuildCatalog = {
  access: "writer" | "readOnly";
  products: CatalogProduct[];        // every reviewed product in registry order, then the unreviewed ones (below), once each
  summary: CatalogSummary | null;    // newest stored summary; null = none recorded
};
type CatalogRefreshFailure = {
  productId: string | null;          // null = the summary
  document: "versions" | "cdns" | "summary";
  state: "unavailable" | "refused" | "storeFailed" | "unsupported" | "needsNetwork";
  code: string | null;               // the layer 2 code, e.g. CDN_OFFLINE
  message: string | null;            // at most 512 chars
};
type BuildCatalogRefresh = {
  catalog: BuildCatalog;             // the list after the refresh; access is always "writer"
  refreshedAt: string;
  checked: number;                   // products asked, reviewed and unreviewed
  failures: CatalogRefreshFailure[]; // the summary's first; at most 160, every document a refresh asks; [] = everything answered
  newProducts: string[];             // what this refresh's summary says is new; [] when the summary failed
};
```

Times are ISO 8601 with an offset (`SettingsTimestamp`).

A failure is a layer 2 state other than present or not found. Not found is an answer and is never a failure, as `vm browse` counts it. Each product's own problems come back in `catalog.products[i].problems`.

## access

`writer`: the Host holds the object store's writer lock. `readOnly`: another process, such as `vm browse`, holds it, and the list was read from the store read-only. `catalog.list` never fails for the lock; it answers `readOnly` instead.

## Refresh

`catalog.refresh` asks for the summary first, then refreshes every listed product, at most 3 at once, then answers the same projection as `catalog.list`. What each product observed is kept in the history log as it finishes.

Listed products ([D476](../docs/DECISIONS.md)): every reviewed one in registry order, then the WoW products the summary lists that aren't reviewed (ids `wow` then up to 61 of `a-z 0-9 _ -`), in natural order (`wownev2` before `wownev10`), at most 64. `catalog.list` takes them from the newest stored summary, and a refresh from the summary it just fetched, or the newest stored one when that fetch failed. An unreviewed product is labelled by its id and is otherwise a product like any other: its builds are listed, refreshed and opened the same way. A product the newest summary no longer lists drops out of the list; its history stays and comes back if a summary lists it again.

Progress: `{ stage: "Checking the version service", completed, total, unit: "products", region: null }`. The first frame is `completed` 0 of `total` 1, before the summary. Once the summary is in, `total` becomes the listed product count plus 1 and `completed` 1, then one frame per finished product up to `total`. No percentage, bytes or ETA.

Cancel: `request.cancel` stops starting products and cancels the running ones. After they retire the reply is `CANCELED`, retryable: "Refresh stopped. Products checked before stopping keep their new results."

## Errors

| Code | Retryable | When |
| --- | --- | --- |
| `OBJECT_STORE_BUSY` | yes | `catalog.refresh` only: another process holds the writer lock. "Another VibeMiner process, such as vm browse, is writing the build store. Close it, then refresh." |
| `BUILD_STORE_UNAVAILABLE` | yes | The store can't be opened; the layer 1 code is in the message, e.g. "The build store can't be opened (OBJECT_STORE_NEWER)." |
| `CANCELED` | yes | `catalog.refresh` stopped (above). |
| `INVALID_REQUEST` | no | Any parameter. |

## Bounds

At most 100 products (14 reviewed and 64 unreviewed at most), 100 builds and 20 problems per product, 64 regions per build, 160 failures (every document a refresh can ask: 157) and 64 new products. The whole reply, envelope and worst-case request id included, fits the 1 MiB frame, measured as the Host writes it (escapes such as `\u0027` count 6 bytes). The Host stops adding builds once their JSON would pass 750,000 bytes. If the reply is still too large, it drops builds from the last products first, then problems the same way, until it fits. `buildCount` and `problemCount` still give the totals, so a short list says how much was left out. Failures and new products are never dropped; they are small enough at their caps.

Examples: [build-catalog.json](examples/build-catalog.json) and [build-catalog-refresh.json](examples/build-catalog-refresh.json).
