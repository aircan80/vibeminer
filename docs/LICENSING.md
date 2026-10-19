# VibeMiner licensing

Original VibeMiner software, project documentation, and original artwork continue under **GPL-3.0-or-later**, carrying forward the Java project's selected license. [LICENSE](../LICENSE) contains the GPL version 3 text and [NOTICE](../NOTICE) states the or-later choice and attribution.

The foundation includes original project mockup HTML and the original vector icon from the Java source checkpoint. It also includes a separately attributed pinned WoWDBDefs manifest and twelve unmodified table definition files for Tables metadata browsing. No game payloads, TACTSharp/DBCD parser code or Java runtime are included.

## Reference and dependency responsibilities

TACTSharp and DBCD are MIT-licensed research references. We write our own parsers and acquisition code; reference study does not add a dependency. Preserve attribution and applicable notices for any separately authorized reuse of upstream material. Record immutable reference revisions with the affected format work.

Standard .NET, Electron, SQLite bindings, React, Three.js, and build/test tools retain their own licenses if introduced. Inventory exact dependencies and notices during bootstrap and packaging. Do not reuse the Java distribution notice verbatim: it describes bundled Java runtimes and schema resources that this foundation does not ship.

WoWDBDefs definition data is bundled at revision `b9a63096cdd99b26967f17f9515377ae62cadfd7`: the provider manifest plus twelve DBD files selected in the resource manifest. These are community-maintained metadata, not acquired Blizzard table rows or parser code. Credit **WoWDBDefs contributors**, retain their CC BY-SA 4.0 terms, original unmodified bytes, immutable source URLs/hashes and NOTICE/LICENSE in [the resource directory](../src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/b9a63096cdd99b26967f17f9515377ae62cadfd7/). Our bounded DBD interpretation code is original. Parsed/displayed definition projections retain provider attribution and the definition-data terms; they do not change the license of the application itself. The provider's designated code has separate BSD-3-Clause terms; no such code was imported. Packaging copies the raw resource/licensing directory to `third-party/WoWDBDefs` alongside the embedded resources.

Three.js is pinned to `0.186.1` for graphics only, under MIT; the exact archive is integrity-locked. Its license and development-only TypeScript declaration attribution are retained in [the dependency notice](third-party/three/README.md), which local packaging copies alongside the runtime bundle. No Three asset loader or development typing dependency implements acquisition or game parsing.

## Distribution

Binary distribution must include applicable licenses/notices and corresponding source for the exact release in a form recipients can access. A moving branch or inaccessible private URL is not sufficient source identification. Development builds with uncommitted changes must not claim their base commit fully describes their source.

The project license grants no rights to Blizzard game content or trademarks. Keep downloaded content out of source and release packages. The source repository is public on GitHub. Binary packages are not distributed yet; a release must meet the requirements above.
