# Making a mission with the mission editor

Status: first draft for map makers, written from the editor's code, not from a play-through; **no mission built this way has been run in the game by the author**, so treat the behaviour described as intended, and report what differs.
For maintainers see [MissionEditor](../Systems/MissionEditor.md); for every node see the [node reference](../Systems/MissionNodeReference.md).

## 1. What it is for

The mission editor turns a graph of nodes into the `_script.lua` of your map: objectives, enemy spawns, cutscenes and dialogue, AI bases with attack waves, co-op, a difficulty menu. You connect nodes with wires; on export the editor writes the script. The map's other files are not touched.

It is a **campaign-style** script: it sets up the armies like the game's campaign missions do and loads the campaign AI, so the map should be a map you play against AI armies, not a plain skirmish.

## 2. Opening it

Scenario tab, sub-tab **Mission**, button **Open Mission Editor**. Switch on **Use graph for _script.lua** (also a switch in the panel header); with it off, the graph is only a draft and the export writes the plain script. The panel can be closed with `X` or Escape; everything is saved with the map.

## 3. The panel

- **Stage tabs** (top): a mission is a series of stages (Intro, Stage 1, Stage 2, Outro...). A stage is its own graph. A stage starts when an `Advance to stage` node points at it; the first stage starts with the mission. `+ Stage`, `+ Intro`, `+ Outro` add one; **Bases**, **Variables** and **Settings** are on the right.
- **Library** (left): every node, in categories, with a search box. Click a node to add it to the canvas.
- **Canvas** (middle): drag empty space to pan, wheel or `+`/`-` to zoom. **Home** (or the **Home (fit all)** button) shows every node, which brings you back when you have wandered off; **F** centres the selected node. Drag a node by its header; when you hold it (or a wire) near the edge of the canvas the view scrolls along. The green **Start** block is where the stage begins.
- **Adding a node from the canvas**: drag a wire from an output port and **let go over empty space** - a search box opens; type part of a node's name, use Up/Down, press Return (or click) and the node appears there, already connected. A **double click** on empty canvas opens the same search without a wire. Escape cancels.
- **Properties** (right): the selected node's parameters; Duplicate and Delete.
- **Bottom**: **Generated Lua** (what will be written; `Copy Lua`), **Validation** (problems; a click on a line jumps to the node), **Mission Log** (what you did, compiled and exported).
- Undo and Redo in the header, or Ctrl+Z and Ctrl+Y. Delete removes the selected node or wire.

## 4. Nodes and wires

A node has **inputs** on its left and **outputs** on its right. Drag from an output port to an input port to make a wire; the node at the end runs when the node at the start finishes through that output. Several wires may leave one output (they run one after the other) and several may reach one input.
Click a wire to select it; right-click or Delete removes it.

Most nodes have one input and one output (`out`). Others have several outputs: `true`/`false`, `completed`/`failed`, `killed`... Some have several inputs, listed under the title (`start`, `next`, `break` on a loop). A node marked *(later)* in the reference only **arms** something - a timer, a trigger, an
objective, a dialogue - and its outputs run later, when it happens. The wire's meaning is always "when this finishes, do that".

Parameters are typed: an army, a unit group, a marker, a chain, an area are dropdowns of what your map has; a number can be a number or `$variable` to read a variable. Names the map does not have are reported in Validation.

## 5. Prepare the map first

The graph points at things by name, so put them on the map before you use them (Objects tab):

- **Armies**: `ARMY_1` is the human player; with co-op the first N armies are players; the rest are enemies. Their order is the order of the `ARMY_*` markers.
- **Unit groups**: a group an army owns (`OUTPOST`, `BASE_100`...). **Keep groups that a Spawn group node creates outside the army's `INITIAL` group**: the game creates everything inside `INITIAL` (subgroups too) when the map loads, so a group inside it exists from the start whatever the graph does.
  A group must hold at least one unit; the validator says so.
- **Markers** (camera targets, base centres), **areas** (for area objectives and triggers), **chains** (patrol routes for waves and for bases' engineers).
- Every army that fights should have units: its `INITIAL` group, or a Spawn group node. An army with no units is reported; an enemy base's attack waves are only built against an army that has units.

## 6. A first mission in two minutes

1. Settings tab, **Samples**, **Destroy the outpost** (it fills in your first enemy army and its group when the map has them).
2. Stage 1: *Spawn group* creates the enemy outpost, *Objective: kill group* asks the player to destroy it; `completed` leads to *Advance to stage* (stage 2), `failed` to *Defeat*. Stage 2: a *Timer* then *Victory*.
3. Look at **Validation**; fix errors (red), read warnings. Export.

Other samples: **Hold the line (co-op)** (cutscene, dialogue, enemy base with an OpAI raid, variable counting, solo or co-op ending) and **Loop: three waves** (a counted loop).

## 7. Recipes

**Objectives.** The *Objectives* category: kill, protect, capture, kill or capture, units in an area, a timer, an army statistic, a research, looking at markers, and a manual one. An objective gives `completed` and `failed` outputs. *Resolve objective* finishes one from another place by its node number.

**Spawn, orders, units.** *Spawn group* creates a group and remembers it; *Give order* and the many *Units* nodes (move, attack-move, patrol, give to an army, veterancy, health, shields...) act on a group or a unit by name.

**A cutscene.** Use the NIS nodes: *Cutscene: begin* ... camera moves, dialogue, fades ... *Cutscene: end*. The begin node locks the player's input and shows the black bars; **the end node gives the game back - a cutscene that is begun and cannot reach an end node is reported**. The *simple cutscene* nodes
(begin, camera move, end) are the short form of the same thing.

**Variables and branches.** Variables tab: name, type (number, true/false, text), start value. *Set variable* changes one; *Branch on variable* has `true`/`false` outputs; *Switch on a variable* has an output per listed value; *Counter* counts what reaches it.

**Co-op.** Settings: switch Co-op on, set the number of human slots (1 to 4), shared or separate unit cap, and when the mission fails (any player loses the commander, all of them, never). *Branch: solo or co-op* sends the flow one way or the other. **Co-op is optional at play time**: if the lobby leaves a player slot empty, that army is skipped.

**Difficulty menu.** *Flow: Ask the player for the difficulty* shows a small menu (three buttons by default) when it runs and continues from its `chosen` output. The answer (1, 2, 3) is stored in the game's difficulty setting and, if you choose, in a variable you can branch on. Only player 1 can answer unless you
switch that off; a time-out can pick a default. **The bases are built after the answer**, so they follow the difficulty. The game keeps running while the menu is open: put the rest of the mission after `chosen`.

**AI bases (Bases tab).** A base is an enemy base the campaign AI builds and defends: its army, a marker for its centre, a radius, the groups it can rebuild from (a priority each), how many engineers, whether it starts filled (units already there) or empty, and its **OpAI** attack waves (a type such as `LandAttackCybran`, a name, the route - chains
it patrols or random points -, how many units a wave has, how many waves at once, a delay between attacks, enabled or not). Rules the editor checks for you: **a base that starts filled needs a chain named `<BaseId>_EngineerChain`** on the map (the engineers patrol it); its build groups must exist and hold units. Nodes can start a base later,
switch it or its OpAI on and off, change a wave's size, make one base an expansion of another.
For waves to be built the target army (by default `ARMY_1`) must have units, the enemy's research must be unlocked (the army setting **Unlock all research**, on for enemies by default), and the base's build groups must contain a factory of the OpAI's faction (UEF, Cybran or Illuminate).

**Loops.** The *Loops* category:

- *Repeat N times*: runs its `body` output N times. Connect the end of the body back to the loop's **`next`** input - that is "this round is done, go on" - or switch on **Next round by itself** (then it moves on without waiting, after the pause). `done` fires after the last round, `stopped` after `break`. A variable can hold the round number.
- *Repeat while a variable matches*: the same, while a variable compares true (with a pause of at least 0.1 s and a limit on rounds).
- *Repeat every N seconds until stopped*: a ticker with `start`, `stop`, `pause`, `resume`.
- *For each unit in a group*: takes the units one by one; the `loop_unit_*` nodes (kill, remove, stop, move, attack-move, patrol, give, veterancy, health, "is it of a kind?") act on the **current** unit - give each its **Loop node number**, the `#` of the *For each unit* node.

Other flow nodes with several inputs or outputs: *Wait for all inputs* (inputs 1..N), *Sequence* (outputs 1..N, in order; or with pauses), *Switch on a variable*, *Gate* (inputs `pass`, `open`, `close`), *Counter* (`count`, `reset`).

**Raw Lua.** The last category: a free Lua node for anything the library lacks. The editor's generated regions must not be edited by hand (the Generated Lua tab says when a region no longer matches the graph).

## 8. Settings

- **Armies**: for each army of the map: its script name, role (player, enemy, generic), colours (`AARRGGBB`), unit cap, resource boosts or starting amounts, research, and whether the AI's research is unlocked. Defaults follow the Termesis community mission.
- **Test from this stage**: set a stage as the test start and the next export begins the mission at it, skipping the earlier stages. **Clear it before you release the map** (Validation reminds you; it also shows on the Scenario tab).
- **Samples**: load a ready-made graph (replaces the whole mission; undoable).

## 9. Validation and export

**Errors** (red) stop the export: **an export with a broken graph fails** with a message instead of writing a map without its mission. Fix the errors, or switch the graph off to export the plain script. **Warnings** do not stop it but usually mean something will not work: a node that can never run,
a name the map does not have, a loop with nothing wired to `next`, an army without units, a cutscene that cannot end. Read them all.

## 10. When something does not work

| Symptom | Likely cause |
|---|---|
| Export says the mission graph has errors | open Validation; every error line names the node |
| The enemy base is built but no attack units | the target army (`ARMY_1`) has no units at the time (give it units or a Spawn group); the enemy's research is locked; or the OpAI is switched off |
| A group is on the map even when its Spawn node did not run | the group lies inside the army's `INITIAL` group; move it out |
| After a cutscene the player cannot move | the cutscene's end node is missing or unreachable |
| A loop runs once and stops | nothing is wired to `next` and *Next round by itself* is off |
| The game crashes with an empty lobby slot | the mission was exported with an older version of the editor; re-export |
| The mission starts in the middle | a *Test from this stage* is set |
| Units do not appear | the group is empty, the army or group name is wrong (Validation), or the army is not in this game (a lobby slot nobody took) |

The generated script writes `SCED:` lines to the game's log: which armies are in the game and how many units each has, which bases were created, what a Spawn group created, the chosen difficulty. When asking for help, send that log and the Generated Lua.

## 11. What to be careful about

- Everything here is written from the code and the game's own scripts; **run your mission in the game before you release it**. The nodes that were not observed in play include the loops, the difficulty menu, the transports, drones and the Krakken, the cinematic nodes and the attack waves.
- A graph saved by an older version of the editor, where every node had a single input, may need its wires fixed for the nodes that now have named inputs.

## Related

[MissionEditor](../Systems/MissionEditor.md) (how it works), [MissionNodeReference](../Systems/MissionNodeReference.md), [CampaignFramework](../Formats/CampaignFramework.md) (what the game does with the script), [Lua files](../Formats/LuaFiles.md).
