# Security policy

VibeMiner is a one-person, pre-1.0 hobby project. Reports are welcome. Fixes are best-effort.

## Reporting a vulnerability
Use GitHub private vulnerability reporting: the **Security** tab → **Report a vulnerability**. Please don't open a public issue for security problems.

Include the VibeMiner commit or version, your OS, steps to reproduce, and what you expected. For data-triggered bugs, send the product, build ID and file hashes plus a minimal synthetic repro. **Do not attach downloaded game files or encryption keys.**

## What to expect
- Acknowledgement within about 7 days.
- A fix on a best-effort basis. There is no bug bounty.
- Coordinated disclosure: we fix first, then publish a GitHub Security Advisory and credit you if you want. Please allow up to 90 days before going public.

## Supported versions
Only the latest `main` and the latest release get fixes.

## In scope
- **Untrusted downloaded bytes.** Malformed or hostile CDN data, or data in the app's local store, that crashes our parsers (BLTE, TACT manifests, ROOT, WDC5/DB2, BLP, M2/SKIN), makes them do unbounded work or allocation, or runs code. Decompression bombs count.
- **Integrity bypass.** Content stored, shown or marked ready without hash verification, or data from one build or locale appearing as another.
- **Electron boundary.**
  - Escaping the renderer sandbox.
  - Abusing the preload bridge or the `vibeminer-image` / `vibeminer-geometry` protocols (for example, path traversal or reading files outside app storage).
  - Bypassing the content security policy.
- **Engine IPC.** Renderer-supplied paths, URLs or build IDs reaching the filesystem or network without validation.
- **Dependencies.** Known vulnerabilities in bundled packages that are reachable from the app.

## Out of scope
- Blizzard's services, CDN or game client. Report those to Blizzard.
- The app downloading public game data from Blizzard's CDN. That is its purpose.
- Encrypted game content and TACT keys. Being unable to decrypt something is not a vulnerability.
- Attacks that need an already-compromised machine or user account.
- Load testing or denial of service against Blizzard's CDN.
- Dependency advisories that the app cannot reach.

## Ground rules
Good-faith research is welcome. Don't hammer Blizzard's CDN while testing, and don't publish game content as part of a report.

## What isn't there
VibeMiner has no accounts, no Battle.net login, no stored credentials and no server. It talks only to Blizzard's official public version service and CDN, and keeps everything in local app storage.
