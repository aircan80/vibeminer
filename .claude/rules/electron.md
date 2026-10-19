---
paths:
  - "apps/desktop/**"
  - "tests/desktop/**"
  - "contracts/**"
---
# Electron and React rules

- Renderer and preload code never fetches from the CDN, runs SQL or parses WoW data. The C# engine does that work and returns bounded projections.
- Preload exposes narrow operations through context isolation. It never exposes arbitrary IPC, SQL, HTTP or path access.
- Treat everything crossing the bridge as untrusted. Validate payloads against the schema.
- Custom protocols (`vibeminer-image`, `vibeminer-geometry`) serve only engine-approved handles. They allow no path traversal, no extra privileges and no CSP relaxation. See the Electron boundary section in [ARCHITECTURE](../../docs/ARCHITECTURE.md).
- Drop stale results after a product, build or locale change. Async completion never moves focus, scroll or selection.
- Large binary data never travels as JSON or base64. Use the approved handles.
- Follow [UI-GUIDELINES](../../docs/UI-GUIDELINES.md).
  - Discovery follows `docs/design/desktop-discovery/index.html`. Preparation, Home and top navigation keep their selected references.
  - Keep its HTML structure, fonts, sizes, weights and colours.
- UI changes need:
  - real Electron captures compared with the reference at matching sizes
  - keyboard and focus checks at 100% and 200% text
  - loading, empty, offline and error states
- Remove prototype-only controls. Label fixture data visibly.
- Contract changes update `contracts/` (schema, generated types, examples) together. Run `npm run contracts:check`.
- Any app run used for testing sets `VIBEMINER_DATA_DIR` so the owner's real profile is never touched.
