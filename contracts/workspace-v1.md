# Workspace configuration admission v1

Historical configuration adapter capability: `home-configurations-v1`. Production now uses [download-size Home](download-v1.md). This supersedes the provisional local-context-only contract. Home entry requires actual verified build/CDN configuration; it does not require the full resolver-metadata closure.

## Request and source authority

`workspace.open` takes exactly `{ productId, buildIdentity, sourceRegion }`. The identity is the complete 64-character lowercase digest, not the display build number. Region is an explicit visible choice backed by saved versions/CDN receipts. C# resolves and pins the newest coherent committed observation for that exact product/identity/region before work starts, and verifies the full raw versions row plus corresponding CDN row. Missing requested sources fail rather than fall back to US or another build.

The result echoes `sourceReference`: exact decimal-string generation, region, both document SHA-256 digests and both row ordinals. Identical-byte historical observations may be resolved deterministically to the newest matching observation; the output identifies the one actually used. Renderer-supplied Build objects, URLs, paths, SQL and raw config data are not accepted.

## Success and proof boundary

`WorkspaceSnapshot` is canonical in the shared schema and generated TypeScript:

- `build`: trusted complete saved Build projection, including unknown identity fields and discovery receipts.
- `openedAt`: successful admission/revalidation timestamp.
- `preparationProfile: home-configurations-v1`, `readiness: configurationReady`, `metadataState: configurationsVerified`.
- `configurations`: exactly one buildConfig and one cdnConfig receipt. Each includes its advertised key, physical SHA-256, byte length, actual source URL, original retrieval time, current verification time and parsed-field count.
- `sourceReference`: the engine-selected original event/row pair described above.
- `inventoryState: notPrepared`, `retentionState: unknown`, `downloadPolicy: onDemand`; researchLocale/platformScope are null.

Both configuration bodies must match their raw MD5 filename identity and supported strict text grammar before shared storage/catalog admission. Unknown fields remain preserved, without claiming their referenced families have been acquired. Do not send full configuration maps/raw bodies over control IPC. Healthy cache reuse preserves acquisition URLs/times; current local verification does not masquerade as a new download.

No EN, ROOT, IN, DL, DS, archive, game-file or asset acquisition is authorized by this profile. Their support/preparation remains explicit. Configuration readiness is scoped Home readiness, not an inventory or full-build readiness claim. Local/research-prepared flags remain separate. Successful actual Open acknowledgement may commit with this scoped admission.

## Lifecycle and failure

Use the normal correlated request, progress and terminal envelopes. Progress reports actual checking, configuration acquisition/verification and publication, with measured counters only when available. There is no invented overall percentage, ETA, indexing stage or minimum animation duration.

Back detaches observation while one producer continues. Matching Open reattaches; a different context must not silently replace owned work. Only an attached matching generation navigates Home. Cancel acknowledgement differs from the target's terminal retirement; durable publication can win a late cancellation. Shutdown retires the producer and closes resources.

Missing exact build/source uses typed not-found/source errors; invalid arguments use INVALID_REQUEST/INVALID_PRODUCT; failed saved proof uses CATALOG_INTEGRITY. Unsupported/untrusted routes, redirect/status/size/time failures, raw hash mismatch, malformed config, object corruption, storage/publication failure and cancellation remain distinct failures. No failure enters Home or substitutes another build.

A healthy retained admission revalidates locally with zero HTTP. Initial retry may reuse valid completed configuration objects. Missing/corrupt previously admitted bytes do not trigger automatic repair under ordinary Open. File publication and SQLite admission are separately recoverable resources.
