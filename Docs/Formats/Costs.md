# `costs.bdf` (movement navmesh grids)

Code: `Assets/SC2/Runtime/BdfIO/Formats/Costs/CostsBdf.cs`. Container: [Container](Container.md), padding `MinPad32`, 1 file, no string pool. The editor's generation, block sets, painting and validation rules are in [Costs](../Systems/Costs.md); the file is produced by `BuildCostsModelFromPaintedData`.

## CostsHeader (12 bytes)

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `Version` | 2 |
| 4 | 4 | block count | 3 or 5 on all 56 vanilla maps (30 and 26); the editor writes 3 or 5 |
| 8 | 4 | blocks offset | pointer to the first block header (12); the last offset-table entry |

## CostsBlockHeader (28 bytes, one per block, right after the header)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | `BlockIndex` |
| 4 | 4 | navmesh size in bytes |
| 8 | 4 | navmesh offset (pointer) |
| 12 | 4 | island map size in bytes |
| 16 | 4 | island map offset (pointer) |
| 20 | 4 | number of trailing uint32 values (`LongInts`) |
| 24 | 4 | `LongInts` offset (pointer) |

## Block data, in sequence after all block headers

For each block: **navmesh**, then **island map**, then **LongInts**.

- **Navmesh**: 8-bit grayscale, one byte per cell; the size equals the island map size, usually width times height.
- **Island map**: 8-bit, the connected components of the navmesh, indices 0 to 254.
- **LongInts**: an arbitrary number of trailing uint32 values per block. Measured across the 56 vanilla maps (30 with 3 blocks, 26 with 5): the count per block takes more than a dozen distinct values from 4 to 112 (4 in 94 blocks, 8 in 22, 20 in 21, 24 in 15, 16 in 12, 12 and 60 in 11 each, and others); the code comment's "28, 28, 28, 24, 4" is one example map. Their meaning is not confirmed; the editor preserves them.

## Offset table

`numBlocks * 3 + 1` entries, confirmed byte-for-byte: for each block in order, the navmesh offset field, the island map offset field and the `LongInts` offset field (header fields in declaration order); then one final entry, the header's blocks-offset field (position 8).

## Rules

- Navmesh and island map of a block have the same length.
- The grids are per movement type; which block is which (block names, labels) and which are painted versus derived is editor knowledge, see [Costs](../Systems/Costs.md).
- `LongInts` must be copied through unchanged for a block the editor does not regenerate.
