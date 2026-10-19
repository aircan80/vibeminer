# VibeMiner development tools

Repository-owned build, contract generation, verification, screenshot and packaging helpers live here. Use the actual commands and prerequisites in [BUILDING.md](../BUILDING.md).

The explicit [inventory matching benchmark](benchmarks/InventoryMatching/README.md) compares a compact hashmap, ordered SQLite streams and direct retained metadata. It runs against isolated app-owned development data and is separate from normal deterministic tests. See [its README](benchmarks/InventoryMatching/README.md) and [storage notes](../docs/STORAGE.md).

Helpers must work without private agent storage or the sibling Java checkout. Use app-owned temporary directories, explicit bounded inputs, and deterministic fixtures. Do not bundle development helpers or downloaded game data with the app unless they are declared runtime inputs.
