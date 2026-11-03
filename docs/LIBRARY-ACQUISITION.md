# Owner-bound library acquisition

Issue #226 adds an isolated `LibraryObjectAccessService` over an explicitly supplied mixed library. `ForOwner(owner, scope, producer)` returns an immutable Original `ILibraryObjectSource`. Every view shares one CdnFetcher, scheduler, host ranking, byte budgets and per-key physical work. There is no invented global publication owner. Current app composition is not switched by this service.

Composition's `LibraryAcquisitionService` owns its HTTP clients and the shared service. It drains acquisition before closing those clients; the supplied library stays borrowed. Owner views implement the existing IObjectSource and IStoreCommits ports. Observation fetching/recording is explicitly unsupported here; existing approved-source clients and policy remain separate.

## Ownership and cancellation

Raw library reads validate results before attribution, so a refused length or damaged read cannot gain a new consumer claim. Publication handoff also checks each consumer’s requested range against the actual payload length; an invalid unknown-length range cannot become a publication owner. Every successful whole, batch, range or ensure-style result has its captured owner's durable association before completion. Joined transfers and duplicate keys gain their own claims without rewriting first-committed payload bytes or origin. Pure presence/count and history queries do not assign ownership.

`LibraryObjectOwner.ClaimIndexedAsync` accepts at most 1024 typed namespace-zero keys. One FULL transaction attributes only keys present in the committed index and returns ordered booleans, including duplicate positions. Absent keys remain false. It does not read payloads, alter expected inventories, clear damage or prove verified physical retention. Validation precedes publication; cancellation before the transaction changes nothing, while an entered transaction returns its actual outcome.

A physical producer has independent consumer lifetimes. One consumer's cancellation/disposal leaves other consumers running; an interactive join promotes the shared transfer. Final-consumer cancellation stops and drains the producer, retaining only genuinely proven staging.

Publication handoff selects a live submitted immutable owner capability. A consumer that left before handoff gains no atomic payload claim. After accepted handoff, later departure cannot rewrite an actual committed owner. Other successful consumers receive their own indexed attribution before completion. Streamed handoff releases the old staging handle and re-proves the retained part under the new owner; retarget attempts are bounded.

A complete headerless encoded part is reusable by a library owner only after bounded replay and full key verification. Partial or damaged headerless bytes are not a proven prefix. The verifier is reconstructed for subsequent commit. This does not enable generated raw staging or change the legacy CDN-store resume contract.

## Source evidence and ranges

Requests freeze bounded copies of their route, archive list and locations before asynchronous sharing. A joined consumer's route is not reported as contacted: successful joins report Store with the actual first stored Origin. Failure under a different captured context is Unavailable/ContextUntried, without importing another route's 404 timestamp.

A fully re-proven working part can commit without HTTP; that call reports Store. A pre-existing prefix without source receipts has Unknown origin. Proven bytes produced during the current CDN flight keep that known origin through restaging/owner handoff. The final committed index supplies the returned Origin, so duplicates cannot restamp an existing copy. This is coarse existing source evidence, not a complete per-URL receipt ledger.

Successful ranges require the full verified/stored object first, then a bounded read from it. A verified CDN-only chunk cannot establish ownership of an absent complete object. Existing legacy range behavior remains separate.

## Damage and lifecycle

Known corrupt/lost library objects return typed Refused results. The service never deletes authoritative entries or loops through repair attempts. Session-local damage state is bounded and never evicted into a later silent success; exhausted capacity fails acquisition closed with DamageLimit. Repair/removal remains separate. Pure index-presence facts are not verification claims.

View disposal rejects new calls and retires its accepted consumers and subscriptions. Service shutdown fences all admission and drains transfer, staging, claim and subscription work before releasing owned clients. Neither closes the borrowed library. One shared physical commit feed publishes actual newly stored keys once; duplicate membership claims emit no extra physical Stored event. Bounded subscriptions retain the existing overflow/re-measure behavior.

Default/hard admission limits are 32 views, 64 operations, 1024 keys per batch, 128 physical flights, 32 consumers per flight, 1024 known-damage entries, and 64 subscriptions with capacity at most 1024 each. Staging retargets default to four, capped at 32. Request snapshots bound locations at 64, hosts at 16 and archives at 8192. Batch workers and the existing HTTP/staging byte budgets bound execution; options may lower the admission limits.

## Owner-aware merged archive batches

Issue #227 extends the same service with bounded archive groups. Qualified adjacent members use the existing CdnFetcher planner, range validation, scheduler and byte budgets. Loose or unsuitable requests retain the bounded individual path. Lower Order groups finish before higher groups are admitted, and results retain input positions and duplicate semantics.

Groups reserve individual member consumers before scheduling. Stored, already-pending and abandoned members are rechecked at admission; retained location metadata lets the planner cut around excluded members. Receiving gap bytes is not permission to publish an unrequested object. A merged response must pass its range/length checks, and every published member must pass its own key check.

The synthetic integration fixture requests two members around an unrequested middle member in one HTTP range, publishes only the requested keys and counts the range bytes once. Retryable merged failures retain the existing individual retry policy: a truncated archive can still yield independently verified complete members, while the truncated member remains absent.

Each member retains independent owner/cancellation state. A departure removes only that member's demand; network cancellation occurs only when no live group demand remains. Interactive joins promote existing work. Publication chooses each member's actual live, valid captured owner and attributes surviving consumers before success. Group and member retirement precede shutdown resource release.

Group wire-byte progress is charged once, including actual transferred gaps, rather than copied into every member's logical total. Joined routes, stored Origin, untried-context failures and first-committed bytes retain the #226 evidence rules. Group limits add at most eight groups and 64 members per group; admission also respects remaining flight capacity and existing bounded range bytes.

## Remaining S10 work

Both individual and qualified merged archive acquisition are explicit headless services. Current app/bulk adoption remains separate; request-count and cancellation evidence for this path comes from #227, rather than legacy planner results. Automatic scheduling, current profile cutover, import mapping, generated raw staging, cleanup/GC and retention/source-policy changes are outside this slice.
