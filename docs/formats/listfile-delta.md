# Listfile delta (VMLD, layer 4)

A wow-listfile release (D105) stored as its changes to the release before it (#186). A chain is one full copy, then up to 16 deltas, each against the previous release. Code: [`ListfileDelta`](../../src/backend/VibeMiner.Files/Names/ListfileDelta.cs). It is a pure codec: no I/O, no storage.

## Units

The CSV is split into a **lead** and **units**, on raw bytes:

- An **id line** starts with decimal digits and `;`. The digits must be a canonical FileDataID (no leading zero, at most 4294967295) and strictly ascend through the file.
- The **lead** is every byte before the first id line. Normally empty.
- A **unit** is the bytes from the start of one id line to the start of the next one. It carries its own LF or CRLF and any lines after it that aren't id lines (blank ones). The last unit may lack a newline.
- A line, unit or lead over 64 KiB is refused (a real line is at most 1,036 bytes).

An id line out of order or not canonical, or anything over 64 KiB, refuses the file with `ListfileFormatException`. The caller then stores a full copy.

## Layout

All integers are little-endian. A varint is unsigned LEB128, at most 5 bytes, value at most 4294967295.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `VMLD` |
| 4 | 2 | Version: 1 |
| 6 | 2 | Flags: 0 |
| 8 | 32 | SHA-256 of the base CSV |
| 40 | 32 | SHA-256 of the target CSV |
| 72 | 8 | Target size, i64, 0 or more |
| 80 | 4 | Lead length L, at most 64 KiB and at most the target size |
| 84 | L | The target's lead, whole |
| 84 + L | 4 | Op count N |
| 88 + L | . | N ops |

Op:

| Field | Notes |
| --- | --- |
| u8 kind | 1 Set, 2 Remove |
| varint id step | FileDataID minus the previous op's; the first op's from 0. Only a first op on FileDataID 0 has step 0 |
| varint length | Set only, 1 to 64 KiB |
| bytes | Set only: the target's unit, which starts with `<id>;` |

Ops are in strictly ascending FileDataID order. The delta ends exactly after the last op.

- **Set** adds a FileDataID the base doesn't have, or replaces a unit whose raw bytes differ.
- **Remove** drops a FileDataID the base has.
- Unchanged units aren't stored. An unchanged release is an 88-byte delta plus the lead, with no ops.

Target = lead + for each FileDataID in order: the Set bytes, else the base unit unless it is removed. The base's lead is dropped.

## Encode

A merge over both unit streams by FileDataID, reading both CSVs as streams. Memory is the delta plus a few 64 KiB buffers. Past the caller's maximum delta size it returns null, and the caller stores a full copy. Streams whose bytes don't match the SHA-256 or size given are `InputMismatch`.

## Apply

Streaming. Base chunks of any size go in, target chunks go out to an `IBufferWriter<byte>` as they are made. A chain feeds each level's output straight into the next, so no level holds a whole release. Output is the target only once the last level completes.

Errors (`ListfileDeltaException`):

| Problem | When |
| --- | --- |
| `Malformed` | Bad magic, version, flags, sizes, an unknown op kind, ops out of order, an oversized number, a Set whose bytes don't start with its id, truncation or trailing bytes (all refused before any output). Also a Remove of a FileDataID the right base doesn't have |
| `BrokenChain` | The base's SHA-256 isn't the delta's, including a base that doesn't split; or two links whose target and base hashes don't meet (refused before any output) |
| `WrongTarget` | The output's size or SHA-256 isn't the delta's |

Until the base's hash is known, a bad op can't be told apart from a wrong base. So a level that hits one stops writing, only hashes the rest of its base, and reports at completion: `BrokenChain` if the hash is wrong, otherwise the problem.
