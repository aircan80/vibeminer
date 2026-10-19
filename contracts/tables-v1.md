# Tables definition catalogue v1

Additive capability `tables-definitions-v1`. This is local pinned community definition browsing for an opened exact build. It does not advertise a table-row API, acquired DB2 files, header compatibility or supported decoding. SourceState is `notChecked`; rowsState is `notPrepared` even when a definition supplies one exact-build candidate.

## Requests and identity

`tables.catalog` accepts exactly `{productId, buildIdentity, sourceRegion, query?, limit?, after?}`. Query is literal metadata text, including the empty string, at most80 characters without controls. Limit defaults50 and is1..100. After is an optional canonical nonnegative filtered-list ordinal decimal within Int32. It does not accept SQL, paths, URLs or record filters. The provider revision/order is immutable within this capability. New query resets paging. Responses echo exact query and context, provider proof, bounded entries, filtered totalCount and nextCursor.

`tables.definition` accepts exactly `{productId, buildIdentity, sourceRegion, tableId}`. TableId is the provider's canonical lowercase eight-hex table hash, not a filename, FileDataID or row identifier. A saved exact build/source with an existing Home admission is required. The application never silently selects another build/region or treats region as locale. No new research locale is chosen while browsing definitions.

Catalogue entries retain nullable provider name and DB2 FileDataID. Names and FDIDs come from the pinned manifest; neither proves a selected-build file is present. Entries with bundled definition data sort first, followed by provider name and stable hash. Search covers name, hash, DB2 FDID and column names from the four bundled definitions. It is not complete cross-table field or record search. Counts describe this provider catalogue or its filtered metadata results, not selected-build tables or game records.

## Definition structure and proof

A snapshot echoes exact context and the saved buildVersion, entry, provider, nullable definition and typed problem. Definition contains original source SHA-256/URL, universal COLUMNS and zero or more exact-build candidate layouts. The original bounded DBD reader validates full bytes/grammar and preserves width/sign/array size, ID/noninline/storage-relation annotations, unverified-name markers, comments and explicit target table/column separately.

COLUMNS lists provider declarations across versions. A candidate selects field order/annotations/width only for exact numeric four-component BUILD selectors and ranges. Missing selectors do not fall back to latest/nearby. Multiple/unspecified/conflicting layout interpretations remain explicit; a definition preview cannot choose a readable schema without actual source/header admission. An unverified `?` means uncertain community naming, not nullability. Noninline ID and storage-relation flags are not guessed SQL/FK constraints.

Bundled resources are WoWDBDefs revision `b9a63096cdd99b26967f17f9515377ae62cadfd7`: full manifest (1,346 provider entries;1,270 with DB2 FDID), and unmodified Item, ItemClass, Material and ItemGroupSounds definitions. Original digests/source URLs and upstream license/attribution accompany the resources. Runtime verifies pinned hashes before exposing metadata. No network refresh changes an existing interpretation.

Typed unavailable outcomes include `DEFINITION_NOT_BUNDLED`, `SCHEMA_BUILD_NOT_LISTED` and `SCHEMA_LAYOUT_UNVERIFIED`; invalid context/cursor/provider bytes use failures. None is successful empty record data. Not prepared, missing actual source, encrypted, unsupported decoding, absent rows and zero complete rows remain different later data states.

## Presentation and lifecycle

Top navigation switches Home, Tables and available Download objects while retaining exact context. Tables owns its searchable catalogue, Data/Structure/Relationships panels, inspector and definition investigation trail. Actual records are unavailable. Structure is useful immediately for bundled metadata. Following a declared edge opens the target definition and highlights the annotated target column; it performs no numeric row query, download or label enrichment. Back/Forward restores local table/tab/field/candidate state within a bounded trail. A catalogue selection starts a fresh trail.

The renderer performs no DBD parsing, acquisition or SQL. Main validates request/result schema and exact context; per-view request generations, provider revision and selected build version fence stale definition/catalogue responses. Switching areas preserves local state. Queries remain bounded local work and do not cancel shared build producers or schedule a second downloader. Failed/stale detail reads never relabel a prior definition as the new selection.

The next admitted-data cohort must independently verify source/header/layout, exact research locale, decoder modes and physical/copy/excluded scope before adding record pages and row relationships. `Item.ClassID` targets `ItemClass.ClassID`, not its primary key; `ItemGroupSounds.Sound` has no declared FK. Optional descriptive labels and complete target-domain uniqueness remain later scoped work.
