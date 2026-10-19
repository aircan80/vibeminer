# License texts for bundled NuGet packages

NuGet packages declare a license expression but don't ship its text. Release packaging copies the matching text here into `resources/legal/third-party/nuget/<id>.<version>/LICENSE.txt`, adding the package's copyright line from its `.nuspec`. `--release` fails on a package whose license expression has no text in this folder.

| File | Source | SHA-256 |
| --- | --- | --- |
| [Apache-2.0.txt](Apache-2.0.txt) | https://www.apache.org/licenses/LICENSE-2.0.txt | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |
| [MIT.txt](MIT.txt) | SPDX license-list-data `text/MIT.txt` | `b05785f9f18e6716bab63424b11454513b9943a222595b70411009202fc592b5` |

The MIT template's `Copyright (c) <year> <copyright holders>` line is replaced with the package's copyright. Keep both files unmodified otherwise. Add a text here before adding a NuGet package under another license.
