# VibeMiner licensing

Original VibeMiner software, project documentation, and original artwork continue under **GPL-3.0-or-later**, carrying forward the Java project's selected license. [LICENSE](../LICENSE) contains the GPL version 3 text and [NOTICE](../NOTICE) states the or-later choice and attribution.

The foundation includes original project mockup HTML and the original vector icon from the Java source checkpoint. It also includes a separately attributed pinned WoWDBDefs manifest and twelve unmodified table definition files for Tables metadata browsing. No game payloads, TACTSharp/DBCD parser code or Java runtime are included.

## Reference and dependency responsibilities

TACTSharp and DBCD are MIT-licensed research references. We write our own parsers and acquisition code; reference study does not add a dependency. Preserve attribution and applicable notices for any separately authorized reuse of upstream material. Record immutable reference revisions with the affected format work.

Standard .NET, Electron, SQLite bindings, React, Three.js, and build/test tools retain their own licenses if introduced. Inventory exact dependencies and notices during bootstrap and packaging. Do not reuse the Java distribution notice verbatim: it describes bundled Java runtimes and schema resources that this foundation does not ship.

WoWDBDefs definition data is bundled at revision `b9a63096cdd99b26967f17f9515377ae62cadfd7`: the provider manifest plus twelve DBD files selected in the resource manifest. These are community-maintained metadata, not acquired Blizzard table rows or parser code. Credit **WoWDBDefs contributors**, retain their CC BY-SA 4.0 terms, original unmodified bytes, immutable source URLs/hashes and NOTICE/LICENSE in [the resource directory](../src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/b9a63096cdd99b26967f17f9515377ae62cadfd7/). Our bounded DBD interpretation code is original. Parsed/displayed definition projections retain provider attribution and the definition-data terms; they do not change the license of the application itself. The provider's designated code has separate BSD-3-Clause terms; no such code was imported. Packaging copies the raw resource/licensing directory to `resources/legal/third-party/WoWDBDefs` alongside the embedded resources.

With the Settings switch on (the default), the engine may fetch single WoWDBDefs definition files at runtime ([D081](DECISIONS.md)). They are the same CC BY-SA 4.0 data by the same contributors, used unmodified and never redistributed in packages. The app credits them in NOTICE, in the Settings row and wherever a table uses one ("Definition from WoWDBDefs", with the commit and the CC BY-SA 4.0 credit in the inspector).

The wow-listfile repository has no licence file, so VibeMiner never bundles, commits, ships or uses its listfile in fixtures. With the Settings switch on (the default), each install downloads it from the project's GitHub releases at runtime ([D105](DECISIONS.md)) and keeps it in its own data folder; the names are only shown, never redistributed. The Settings row credits wow-listfile contributors and says where it comes from, and the Files inspector names the release. Tests use small authored listfiles.

Three.js is pinned to `0.186.1` for graphics only, under MIT; the exact archive is integrity-locked. Its license and development-only TypeScript declaration attribution are retained in [the dependency notice](third-party/three/README.md), which packaging copies to `resources/legal/third-party/three`. No Three asset loader or development typing dependency implements acquisition or game parsing.

## Distribution

Binary distribution must include applicable licenses/notices and corresponding source for the exact release in a form recipients can access. A moving branch or inaccessible private URL is not sufficient source identification. Development builds with uncommitted changes must not claim their base commit fully describes their source.

The project license grants no rights to Blizzard game content or trademarks. Keep downloaded content out of source and release packages. The source repository is public on GitHub.

Each package carries `resources/legal/` (`Contents/Resources/legal/` on macOS) outside `app.asar`: LICENSE, NOTICE, `SOURCE.txt`, the WoWDBDefs and Three.js notices, the .NET SDK's `LICENSE.txt` and `ThirdPartyNotices.txt` for the bundled runtime, `npm-packages.txt` with the license files of bundled npm runtime packages, and `nuget-packages.txt` listing the engine's NuGet packages. NuGet packages ship only a license expression, so packaging writes `third-party/nuget/<id>.<version>/LICENSE.txt` from the canonical texts committed in [third-party/licenses](third-party/licenses/README.md) (MIT for Microsoft.Data.Sqlite, Apache-2.0 for SQLitePCLRaw), with the package's copyright line from its `.nuspec`. Electron's `LICENSE` and `LICENSES.chromium.html` are copied to `legal/electron/`. Release-mode packaging fails if any of these is missing, or if a bundled NuGet or npm package has an unknown or unstated license.

The [Release workflow](../BUILDING.md#release) builds draft releases only. Each draft attaches LICENSE, NOTICE, `VibeMiner-<version>-source.tar.gz` (`git archive` of the exact commit) and `SHA256SUMS` next to the installers, and the notes link the commit. The owner publishes drafts by hand.
