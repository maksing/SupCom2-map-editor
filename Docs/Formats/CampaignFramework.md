# What the game's Lua does with a mission script

Status: first draft, read from the game's own Lua (`gamedata/uncompiled_lua.scd`, a zip of readable sources; `lua.scd`/`luo.scd` hold the compiled copy), the campaign and tutorial maps (`SC2_CA_*`, `SC2_CA_TUT_*`), and the community mission
`_CustomMission_1p_TermesisCustomMission_v5.0.0_by_bdftm.scd`; not reviewed by a second reader. **Nothing here was observed in a running game**: it is what the scripts say. It is the knowledge behind the [mission editor](../Systems/MissionEditor.md)'s generated script.
Where a statement is an inference, it says so.

## 1. How the engine runs a map script

A map's `_script.lua` is loaded in the simulation. The engine calls three global functions in it (the campaign scripts all define them), and the debug hotkeys:

| Function | When | What a campaign script does |
|---|---|---|
| `OnPopulate()` | the armies are being created | sets camera defaults and the playable area, calls `ScenarioGameSetup.CAMPAIGN_OnPopulate(camData)`, then `SetupPlayerArmy`/`SetupNonPlayerArmy`/`SetupGenericArmy` for every army |
| `OnStart(self)` | the game starts | plays music, calls `ScenarioGameSetup.CAMPAIGN_OnStart()` |
| `OnInitCamera(scen)` | the camera is initialised | calls `ScenarioGameSetup.CAMPAIGN_OnInitCamera({ funcSetup, funcNIS, funcMain, areaStart })` |
| `OnShiftF3`, `OnCtrlF3`, `OnShiftF4`, `OnCtrlF4`, `OnShiftF5` | debug hotkeys | call `ScenarioTesting` |

`ScenarioGameSetup` (`lua/sim/ScenarioFramework/ScenarioGameSetup.lua`):

- `CAMPAIGN_OnPopulate` runs `ScenarioUtils.InitializeScenarioArmies()`, locks the input, fades the camera to black and updates the camera defaults.
- `CAMPAIGN_OnStart` hides the score of every army except the first and makes the others ignore the playable rectangle.
- `CAMPAIGN_OnInitCamera` forks a thread: `funcSetup()` while the load screen is up, then the optional NIS (`funcNIS`), then `funcMain()` after the game is released; `areaStart` sets the starting playable area.
- `SetupPlayerArmy(index, tuning)` uses `tuning.color`, `unitCap`, `mass`, `energy`, `research` (it takes away the lobby's `InitialMass`/`InitialEnergy` first); `SetupNonPlayerArmy(index, tuning)` uses `color`, `unitCap`, `massBoost`, `energyBoost` and, when `tuning.buffs` is set, picks a buff set by
  `ScenarioInfo.Options.Difficulty`; `SetupGenericArmy(index, colors)`. All of them call `ArmyBrains[index]` directly, so an index with no army is a crash (`nil` indexed).

## 2. Armies and the lobby

`ScenarioUtils.InitializeScenarioArmies` walks `ListArmies()` (index to army name) and, for each army that has an entry in the save file: sets its economy, faction, colour and AI personality, **creates its `INITIAL` group**, creates its `WRECKAGE` group, applies the scenario's alliances and calls
`InitializeCampaignManagers()` on the brain. Facts that follow:

- **`INITIAL` is created with all its subgroups.** `CreateInitialArmyGroup` is `CreateArmyGroup(army, 'INITIAL')`, which creates every unit of the group and of the groups inside it. A group that the editor places inside `INITIAL` therefore exists from the first moment, whatever a script does later; a group that must
  appear on demand has to be a sibling of `INITIAL`, not a child.
- `CreateArmyGroup(army, group)` raises an error when the group does not exist or holds no units (except `INITIAL`); it is the reason for the mission editor's empty-group error. It returns the created units, the platoon tree and the platoons.
- `ScenarioInfo.UnitNames[armyIndex][unitName]` is filled as units are created from the editor's unit names; the generated script finds a single unit through it.
- **Armies are the entries of `ListArmies()`.** Inferred, not observed: an army whose lobby slot nobody took is not in that list, and the armies after it have a lower engine index than their position in the save file; a script that hard-codes `ScenarioInfo.ARMY_x = n` then crashes in `SetupPlayerArmy` or `SetupNonPlayerArmy`. The mission editor looks the indices up by name for this reason.
- The first army (index 1) is the player the AI treats as its target: the campaign AI's attack builders are gated on that army having units (section 4).

## 3. Triggers, objectives, dialogue

- **Triggers** (`ScenarioFramework`, `ScenarioTriggers`): timers, area triggers (`CreateAreaTrigger`, `CreateMultipleBrainAreaTrigger`), unit and group death triggers, unit-to-marker distance, health ratio, damage, army statistics, research, and more. Each takes a callback.
- **Objectives** (`SimObjectives`): `Kill`, `Protect`, `Capture`, `CategoriesInArea`, `Timer`, `ArmyStatCompare`, `Reclaim`... create an objective with `(kind, 'incomplete', title, description, { Units = ..., ... })`; the script adds it to `ScenarioInfo.AssignedObjectives` and gives it a result callback (`AddResultCallback`).
  `ManualResult(true|false)` completes it by hand.
- **Dialogue**: `ScenarioFramework.Dialogue` plays a table of lines (voice bank and cue, video, forced duration); the simple text is pushed to the interface through `Sync.PrintText`.
- **Ending**: `ScenarioGameCleanup.Victory()` / `Defeat()`.

## 4. The campaign AI

A campaign script loads the campaign AI with `doscript '/lua/ai/campaign/system/CAIInitializeSystems.lua'`; it is only loaded for campaign-type maps, which is why every generated script does it unconditionally. After it, each army's brain has `CampaignAISystem`.

- **BaseManager**: `brain.CampaignAISystem:AddBaseManager(id, markerName, radius, buildGroups)`, then `StartNonZeroBase(engineerCount)` for a base that begins filled or `StartEmptyBase(count)` for one that is built from nothing; `AddExpansionBase`, `AddOpAI`, `BaseActive`, `SetEngineerCount`, `SetBuildAllStructures`.
  The base's engineers patrol the chain named `<BaseId>_EngineerChain`; the Termesis mission has it for each of its main bases (the filled ones) and not for its empty expansions.
- **OpAI** (`lua/AI/campaign/OpAI`): attack waves a base builds. The builders are gated on the target army having at least a number of units (the target is army 1 by default), on the faction of the builder matching the army, and on research restrictions; `UnlockAllResearch(armyIndex)` lifts the research restrictions
  (the Termesis script calls it for every AI army). An army with no units at all therefore never gets an attack wave built against it.
- **Difficulty**: `ScenarioInfo.Options.Difficulty` (1 to 3) is read by `BaseManager` (group names get a `_D<n>` suffix, engineer counts are tables indexed by difficulty), by `SetupNonPlayerArmy` buffs, by the transport attack threads and by the score's medal. It is set by the launcher
  (`SinglePlayerLaunch`, `GameSessionManager`) and read when a base is created.

## 5. Cinematics

- **The NIS system** (`ScenarioGameCinematics.lua`, support in `ScenarioGameCinematicsSupport.lua`): `StartNIS_Standard`, `StartNIS_Opening`, `StartNIS_MidOp`, `StartNIS_Victory` and `EndNIS_Standard`, `EndNIS_Opening`, `EndNIS_MidOp`, `EndNIS_NoRestore`, `EndNIS_Victory`;
  camera moves `MoveTo`, `ZoomTo` and their `NoWait` forms; entity tracking, fades, title and location text, unit and group moves, transports (`TransportArrival`), vision markers and more. The start functions set up black bars, the input lock and the saved camera; the end functions give them back. They queue through `NIS_Queue`,
  and the ones that wait must run in a thread.
- **The old module `ScenarioCinematics.lua` is marked obsolete** (`EnterNISMode`, `ExitNISMode`, `CameraMoveToMarker`): it flips `Sync.NISMode` and locks input but does not restore the camera or the interface; a mission that ends a cutscene with `ExitNISMode` is not given back to the player. Use the NIS system.
- Movies and music go through `Sync` (`NISVideo`, `PlayMovies`, `PlayMusic`, `PlayMFDMovie`).

## 6. Talking to the interface: the `Sync` channels

The simulation sends data to the interface by filling fields of the global `Sync` table; `UserSync.lua` reads them every tick. The fields used by the scripts seen: `PrintText`, `PlayMusic`, `PlayMovies`, `NISMode`, `NISFadeIn`/`NISFadeOut`/`NISSwipeUp`/`NISSwipeDown`,
`NISVideo`, `ObjectivesTable`, `ObjectivesUpdateTable`, `ObjectiveTimer`, `AddCameraMarkers`, `ShowUIDialog`, `CreateSimDialogue`, `SetDialogueText`, `UpdateButtonText`, `SetButtonDisabled`, `UpdatePosition`, `DestroyDialogue`, `UICustomScene`, `RequestPlayerFaction`, `Paused`, `PausedBy`,
`LockInput`/`UnlockInput`, `GameResult`, `OperationComplete`, `FloatingEntityText` and others (about 80 in all).

**A menu from the simulation.** `lua/sim/SimDialogue.lua` (`Create(text, buttonText, position)`; also `SimSync.CreateDialogue`) creates a dialogue the interface draws (`lua/ui/game/simdialogue.lua`: a panel with the text and a list of buttons, two to a row; positions `left`, `right`, otherwise the centre). A click is sent
back as `SimCallback({ Func = 'SimDialogueButtonPress', Args = { ID, buttonID, presser = GetFocusArmy() } })`, which `SimCallbacks.lua` routes to the dialogue's `OnButtonPressed(self, info)`. The object has `SetText`, `UpdateButtonText`, `SetButtonDisabled`, `UpdatePosition`, `Destroy`;
`simInit.lua` re-sends the open dialogues after a load. **No campaign mission uses it**, so that it renders and answers correctly in SC2 is unverified. `ShowUIDialog` (used by the tutorials) is a one-button message and returns nothing.

**Pausing.** `SessionRequestPause()` and `SessionResume()` exist only in the interface's Lua (`demo.lua`, `worldview.lua`; the uses in `missiontext.lua` are commented out). Nothing in the simulation's Lua pauses the game: `Sync.Paused`/`PausedBy` only report a pause that already happened, and `SetPaused` on a unit is the
production pause. Also `LockInput()` in `worldview.lua` ends with `SessionResume(true)`, so locking the input (as `CAMPAIGN_OnPopulate` does) would undo a pause. A pause also stops the simulation, which handles `SimCallback`, so a menu would not receive its own click. A mission that shows a menu therefore
keeps running while it is open and must gate its own flow.

## 7. The Termesis mission, as a reference for a custom map

`_CustomMission_1p_TermesisCustomMission_v5.0.0_by_bdftm.scd` is a custom map with a campaign-style script, the model the generated script follows: type `skirmish` in the scenario file; armies `ARMY_PLAYER`... with fixed `ScenarioInfo.ARMY_x = n` indices and
`ScenarioInfo.AssignedObjectives = {}`; colour and `TUNING_*` tables (player cap 1500, mass 2500, energy 6500, research 10; AI cap 1000, boosts 9999999); the `doscript` of the campaign AI; `OnPopulate`/`OnStart`/`OnInitCamera`; thirteen bases, six main ones with an `_EngineerChain` and seven expansions
(`AddExpansionBase`, `StartEmptyBase`) without; OpAI built from platoon templates; `UnlockAllResearch` per AI army.

## 8. How to read the game's Lua yourself

The sources are in `<game>/gamedata/uncompiled_lua.scd` (a zip: `uncompiled/lua/sim/...`, `uncompiled/lua/ui/...`, `uncompiled/lua/AI/campaign/...`, `uncompiled/maps/SC2_CA_*/..._script.lua`). Open it with any zip reader. A custom map archive may be a folder named `<Map>.scd`.
Useful starting points: `lua/sim/ScenarioFramework/ScenarioGameSetup.lua`, `ScenarioGameCinematics.lua`, `lua/sim/ScenarioUtilities.lua`, `lua/sim/SimObjectives.lua`, `lua/AI/campaign/system/BaseManager.lua`, `lua/UserSync.lua`, `lua/SimSync.lua`, `lua/sim/SimCallbacks.lua`.

## Related

[MissionEditor](../Systems/MissionEditor.md), [Lua files](LuaFiles.md), [Objects](../Systems/Objects.md) (armies, groups, markers).
