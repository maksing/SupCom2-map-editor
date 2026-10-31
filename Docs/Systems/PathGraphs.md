# Path graphs and the path grid

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) and [Objects](Objects.md) first.

## 1. Purpose and boundaries

The navigation graphs of the multiplayer AI: path-node markers joined by edges, one graph per movement kind. This document covers the graphs, the Paths sub-tab (edge list, copy between graphs, connect and disconnect
selected markers) and the **Path grid** generator that lays nodes on a regular grid and joins neighbours.

Not included: the markers as objects ([objects](Objects.md)), the costs grids that units path on ([costs](Costs.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| UI and generation | `Scripts/Unified/SC2ObjectsShell.cs`: `DrawPaths`, `DrawPathGridSection`, `GeneratePathGrid`, `PathGridPassability`, `ConnectSelectedMarkersMesh`, `DisconnectSelectedMarkers` |
| Pure grid planner | `Scripts/Core/SC2PathGrid.cs` (`Build`, `Plan`, `MaxNodes`) |
| Edges | `Scripts/Core/SC2ObjectGraphRefs.cs` (`Enumerate`, `AddEdge`, `RemoveAt`, `RemoveEdgesReferencing`, `MigrateLegacyIndexes`) |
| Data | `SC2ObjGraph` (`Name`, `GraphKind`, `MarkerIdsA/B`) in `Scripts/Core/SC2ObjectsModel.cs`; `SC2TerrainData.ObjGraphs` |
| Types | `SC2MarkerTemplates` (Land, Water, Air, Amphibious, Default and Auto Path Node), `ConnectablePathNodeTypes` |
| Export | `SC2ObjectsExport.BuildMapObjsModel` (ids become marker indices at the format boundary) |

## 3. Data

A graph is a name, a kind (Land, Air, Naval, Amphibious), and a list of edges as pairs of **marker ids**. Nodes are markers of the node types; they are shared by graphs. A map has **Land and Air**, plus **Naval and Amphibious** when `PlayableWater`
is on (`EnsureRequiredGraphs` creates what is missing and never removes a graph; an extra one is shown with a warning and can be deleted).

## 4. Flows

- **Connect selected (mesh)**: every pair in the marker selection that has no edge yet is joined (2 selected give 1 edge, N give N(N-1)/2); non-node markers in the selection are ignored. **Disconnect selected** removes edges among them.
- **Copy edges from another graph**: adds the other graph's edges to this one, skipping ones already present; nodes are shared, so no new nodes are made.
- **Path grid**: settings are a step (world units, default 32) and "Only where passable" (on by default). The area is the playable area zone when the map has one, else the whole terrain. `SC2PathGrid.Build` centres the grid in the area, rounds
  positions to whole units, keeps positions that pass the test, and joins each node to its right, lower, and both lower-diagonal neighbours (8 neighbours per node, each pair once); a pair is joined only if three points along it
  (a quarter, half, three quarters) pass too. A grid larger than `MaxNodes` (20 000) is refused.
- **Passability** (`PathGridPassability`): Land = dry ground that is not a wall of the Costs block 0 (or, with no Costs data, a slope within the medium limit of the Costs generator); Naval = water; Amphibious = land or water; Air = everywhere; water
  is height below the water surface.
- **Applying** (`GeneratePathGrid`): one undo checkpoint; a grid position that coincides with an existing marker of the node type reuses it; new markers get unique names; edges already in the graph are skipped; the grid is not mirrored by
  symmetry. The result line says how many nodes and edges were added.

## 5. Invariants

- **Edges refer to marker ids**, so editing the marker list never rewires the graph; the conversion to indices happens only at export.
- **A node type decides the kind of a grid**: Land to Land Path Node, Air to Air Path Node, Naval to Water Path Node, Amphibious to Amphibious Path Node, any other kind to Default Path Node.
- **Generating twice adds nothing**: nodes are reused by position and edges are deduplicated.
- **The planner is pure** (no Unity types); passability is supplied by the caller.
- **The number of graphs follows `PlayableWater`**.

## 6. Editor and build differences

- None specific.

## 7. Extension points and how to change it

- **A new passability rule**: `PathGridPassability` (keep the planner pure). **A new connection pattern**: `SC2PathGrid.Build` (change the neighbour list and the way test, and the tests).
- **A new graph kind**: `GraphKinds*`, `PathNodeTypeFor`, the marker templates and the required-graph rule.

## 8. Editing, verification and tests

**How to edit it safely.** Keep edge creation deduplicated and node creation idempotent. After a change to generation, generate on a map with water and a wall and count nodes per kind.

**Tests today**: `PathGridTests` (open ground, centring, impassable nodes, a way through impassable ground, the node cap, a small rectangle), `ObjectsDocumentBridgeTests`, `ObjectsCommandTests`. A scenario run outside the repository checked
Land, Naval, Amphibious and Air counts on a map with a lake and a steep wall, and idempotence.

**Tests a change must add.** A planner change: a case in `PathGridTests`. A passability change: extract the per-point decision and test it. A generation change: a scenario with counts and a second run that adds nothing.

## 9. Known limitations and unverified behaviour

- The Costs-block branch of the passability test was not exercised (the scenario had no Costs data, so the slope rule ran).
- Imported 3D terrain: heights come from ray casts and the time at 20 000 nodes was not measured.
- Behaviour of the generated graphs in the game's AI was not checked.

## 10. Related documents and review

[Objects](Objects.md), [Costs](Costs.md), [Brushes](Brushes.md) (symmetry), [Export pipeline](ExportPipeline.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
