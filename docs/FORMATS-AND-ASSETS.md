# Formats and assets

VibeMiner implements its own acquisition, protocol readers, WoW table interpretation and asset decoding. This covers backend parsing and any frontend-specific model or texture reader. General platform primitives (.NET HTTP, streams, hashing, compression, JSON) and rendering libraries are building blocks, not substitutes.

## Format reference

| File | Covers |
| --- | --- |
| [formats/tact.md](formats/tact.md) | Discovery documents, configs and declarations, BLTE and its three proofs, EN, IN, DL, DS, archive indexes, CDN fetch rules, locators |
| [formats/root.md](formats/root.md) | WoW ROOT (TSFM), locale/content variants, lookups, selection policies |
| [formats/wdc5.md](formats/wdc5.md) | WDC5 tables, storage kinds, number rules, Sparse, relationships, keyed sections, DBD definitions |
| [formats/blp.md](formats/blp.md) | BLP2 images and the icon PNG pipeline |
| [formats/m2-skin.md](formats/m2-skin.md) | M2/SKIN static geometry and the opaque material profile |
| [ARTIFACT-FORMATS.md](ARTIFACT-FORMATS.md) | Binary files the app writes (row artifacts, projections, indexes, maps) |
| [WOW-DATA-REFERENCE.md](WOW-DATA-REFERENCE.md) | WoW tables, joins and data meaning |

Only the variants listed in those files are supported. Everything else is an explicit unsupported result.

## Reference policy

Third-party parsers (TACTSharp, DBCD, TACT.Net, CascLib, pywowlib, wow.export, WoWFormatLib, BLPConverter and similar) may be studied as references. Pin the exact revision, record the format facts and attribution, and write original code and deterministic fixtures. They are not package dependencies, subprocesses, conversion tools or fallback engines. Do not replace the old Java runtime with another external game parser.

Adopting any reference code would need a documented change to this policy and preservation of its notices. Studying a library does not mean its cancellation, verification, source selection, missing-key handling or cache policy fits VibeMiner. Public-tool behavior is not evidence of how the Blizzard client behaves.

Pinned references currently used:

| Reference | Revision | Used for |
| --- | --- | --- |
| Java VibeMiner | `8f353d046d961108f7acc86bfe1381004467afd5` | Behavioral reference for config, BLTE, EN, ROOT, IN/DL/DS, archive index, WDC5 |
| TACTSharp | `67fd53ac852ca297e9bf95bb41d1a97da202eba4` | TACT/BLTE, EN, ROOT, index kinds |
| TACT.Net | `57618c47dc0cfef1bfc0e7585bf335ced7218389` | DL/DS size semantics |
| CascLib | `38a34665624b8775bb875274b36191b21c38d97b` | Reference only |
| DBCD | `e732093f8864240fc5884bd1bba6b02f3dfc0d56` | WDC5 framing, storage, Sparse, relationships |
| WoWDBDefs | `b9a63096cdd99b26967f17f9515377ae62cadfd7` | DBD definitions (CC BY-SA 4.0, bundled with attribution) |
| BLPConverter | `38fc3229fd95040f5030e735e00c16a39ada7f94` | BLP2 header and encodings |
| Khronos OpenGL Registry | `5f8b6c01d4b486527d66b247984221a82e5b4594` | S3TC/BC block decoding |
| pywowlib | `55276dc5c2195da7fe136638a2a59716622f8c65` | M2/SKIN structures |
| wow.export | `1b625d0f9943be4d8f8e124dbb7a04f1fc7da8f3` | M2/SKIN loading, render flags |
| WoWFormatLib | `1abf1bfad68705a2064b90fba1cb8bcd22588ff0` | M2/SKIN cross-check |

New research must check the exact source before changing an interpretation rule.

## Parser contracts

Pure readers accept explicit bounded spans or caller-owned streams. Validate magic/version, integer arithmetic, offsets, widths, counts, decoded lengths, working memory, string framing and supported flags before allocating or emitting. Keep unknown metadata where safe; refuse unsupported framing honestly.

No parser downloads a missing dependency or opens an inferred game path. CPU loops check cancellation. Streaming callbacks emit provisional records until the complete grammar, counts and EOF succeed. Source authentication and artifact publication belong to the adapters.

Failures keep these apart: malformed, integrity mismatch, unsupported version/layout/mode, encrypted without a usable key, limit, cancellation, I/O failure and source unavailable. A missing key never becomes zero-filled content. No partial decoded object or index is marked ready.

## Staged asset decoding

| Area | Boundary |
| --- | --- |
| Discovery and configs | An advertised pointer is not payload availability |
| BLTE | Encoded integrity differs from decoded CKey proof |
| Catalogs | EN, ROOT, IN, DL, DS and locators are read when a feature needs them; a selected lookup is not a complete closure |
| DB2 | Exact table/layout/schema and record coverage |
| Images | Explicit supported encodings and dimensions |
| Models | Geometry, appearance, animation and portraits are separate capabilities |
| World/audio | WMO, terrain and audio are separate future slices |

This table is not a promise that every area is supported. Keep exact accepted variants and fixtures with each implementation.

## Renderer boundary

C# resolves and verifies dependencies and prepares bounded geometry, texture and animation descriptions. Three.js handles GPU resources, scene rendering, cameras and animation presentation in Electron. Do not send bulk assets as base64 JSON or serialize per animation frame. Measure binary handoff, GPU memory, disposal, context loss and repeated model switching.

Keep WoW material, visibility and appearance information in the internal representation. glTF can be an export format; it must not silently drop unsupported effects or become the only source model.

Full character portraits may need DB2 appearance relationships, replaceable textures, customization, equipment attachments, geosets and animation dependencies. A static creature model does not establish that pipeline. Geometry-ready never means appearance-ready.

## Derived cache identity

Model and portrait cache keys include all source digests, exact interpretation revisions, appearance/customization choices, texture composition, animation/pose/time, camera, lighting, renderer revision and output size/settings. Builds share identical artifacts while keeping their own source context. Original downloaded content stays separate from generated display artifacts. Artifact content keys are described in [ARTIFACT-FORMATS.md](ARTIFACT-FORMATS.md#common-rules).

## Evidence rules

- Authored fixtures first. Tests use original synthetic data; no downloaded game bytes in fixtures, logs or packages.
- Real-source checks are separate and bounded, with the exact build, file and source identity recorded.
- One qualified file is not support for a whole format. One icon does not prove every BLP variant; one model does not prove every M2 version.
- For assets, measure preparation time, allocations, handoff cost, frame responsiveness and repeated-open cleanup, including while a background download runs.

## DBD definition metadata

An original bounded DBD reader and pinned WoWDBDefs metadata support [Tables definition browsing](../contracts/tables-v1.md). COLUMNS declarations and build-selected LAYOUT blocks stay separate from acquired source headers, layout validation and row interpretation. Bundled definitions keep their provider hashes, revision, attribution and CC BY-SA 4.0 terms. Rules: [formats/wdc5.md](formats/wdc5.md#dbd-definitions).
