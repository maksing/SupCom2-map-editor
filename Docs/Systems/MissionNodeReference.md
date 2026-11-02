# Mission node reference

Status: generated from `SC2MissionNodeCatalog.All` (commit `4fa4ae8`, 177 nodes); not yet reviewed by a second reader. Companion of [MissionEditor](MissionEditor.md); for use see the [guide](../Guides/MissionEditorGuide.md).
**When a node is added, renamed or changed, update this page**: `MissionNodeReferenceTests` fails when a node type is missing from it.

How to read the tables:

- **Type** is the key saved in the map (never rename it). **Inputs** are the places a wire can end (`in` unless listed; `1..N` means as many as the named parameter says). **Outputs** are the places a wire can start; *(later)* marks a node that
  only arms something (a timer, a trigger, an objective, a dialogue, a camera move, a loop): its outputs run later, from the game, while a node without it runs its outputs before it returns.
- **Parameters**: label `[kind]`, a default after `=`. A kind is how the value is checked and written: `text`, `number` (a number, or `$variable` to read a mission variable), `bool`, `army`, `area`, `group` (a group of the army named in the node's army parameter), `unit`,
  `marker`, `chain`, `categories` (a Lua expression such as `categories.ALLUNITS`), `stage`, `variable`, `base`, `opai`, `code` (free Lua), `choice` (one of the listed values), and the comma lists `namelist`, `markerlist`, `armylist`. *optional* parameters may be empty; *signed* numbers may be negative.
- Several nodes share a first parameter set: an `army` plus a `unit` or `group` name picks what the node works on (the unit or group must exist on the map for that army).
- The `loop_unit_*` nodes take a **Loop node number**: the `#` shown on the *For each unit in a group* node they belong to.
- Nodes in **Cutscene (NIS)** use the game's own cinematic system (`ScenarioGameCinematics`); **Cutscene (simple)** is a short begin / camera move / end set that uses the same system.

## Events (23)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Timer | `timer` | `in` | `fired` *(later)* | Seconds [number] = 30 |
| Area entered | `area_entered` | `in` | `entered` *(later)* | Area [area]; Army whose units count [army] = ARMY_1; Categories [categories] = categories.ALLUNITS; Units needed [number] = 1; Only once [bool] = true |
| Unit killed | `unit_killed` | `in` | `killed` *(later)* | Army [army]; Unit [unit] |
| Group killed | `group_killed` | `in` | `killed` *(later)* | Army [army]; Group [group] |
| Unit damaged | `unit_damaged` | `in` | `hit` *(later)* | Army [army]; Unit [unit]; Damage at least [number] = 0; Only once [bool] = true |
| Unit health | `unit_health` | `in` | `reached` *(later)* | Army [army]; Unit [unit]; Health ratio (0 to 1) [number] = 0.5; Fires when it falls below (off: rises above) [bool] = true; Only once [bool] = true |
| Unit reached marker | `unit_at_marker` | `in` | `arrived` *(later)* | Army [army]; Unit [unit]; Marker [marker]; Within (distance) [number] = 20 |
| Unit near enemy units | `unit_near_type` | `in` | `near` *(later)* | Army [army]; Unit [unit]; Army whose units count [army]; Unit categories [categories] = categories.ALLUNITS; Within (distance) [number] = 30 |
| Unit made a kill | `unit_killed_unit` | `in` | `kills` *(later)* | Army [army]; Unit [unit] |
| Unit built something | `unit_built` | `in` | `built` *(later)* | Army [army]; Builder [unit]; Built unit categories [categories] = categories.ALLUNITS |
| Unit captured | `unit_captured` | `in` | `captured` *(later)* | Army [army]; Unit [unit] |
| Unit reclaimed | `unit_reclaimed` | `in` | `reclaimed` *(later)* | Army [army]; Unit [unit] |
| Unit selected by the player | `unit_selected` | `in` | `selected` *(later)* | Army [army]; Unit [unit] |
| Unit gained a veterancy level | `unit_veteran` | `in` | `veteran` *(later)* | Army [army]; Unit [unit] |
| Unit is about to die (controlled death) | `unit_dying` | `in` | `dying` *(later)* | Army [army]; Unit [unit] |
| Army unit gained a veterancy level | `army_veteran` | `in` | `veteran` *(later)* | Army [army]; Unit categories [categories] = categories.ALLUNITS; Level [number] = 1 |
| Army statistic | `army_stat` | `in` | `reached` *(later)* | Army [army]; Statistic [choice] {Units_Active/Units_Killed/Units_History/Units_BeingBuilt/Enemies_Killed/Economy_Stored_Mass/Economy_Stored_Energy/Economy_Income_Mass/Economy_Income_Energy/Economy_TotalProduced_Mass/Economy_TotalProduced_Energy/Economy_TotalConsumed_Mass/Economy_TotalConsumed_Energy/Economy_Research_Learned_Count/Economy_Research_Units_Unlocked} = Units_Active; Compare [choice] {GreaterThan/GreaterThanOrEqual/LessThan/LessThanOrEqual} = GreaterThanOrEqual; Value [number] = 1; Unit categories (for Units statistics) [categories] = categories.ALLUNITS |
| Intel gained | `intel_gained` | `in` | `seen` *(later)* | Army that looks [army]; Army that is seen [army]; Intel [choice] {LOSNow/Radar/Sonar/Omni} = LOSNow; Unit categories [categories] = categories.ALLUNITS; Only once [bool] = true |
| Area entered by any of several armies | `area_entered_by` | `in` | `entered` *(later)* | Area [area]; Armies (comma separated) [armylist] = ARMY_1; Unit categories [categories] = categories.ALLUNITS; Units needed [number] = 1; Only once [bool] = true |
| Platoon killed | `platoon_killed` | `in` | `killed` *(later)* | Army [army]; Group [group] |
| Research completed | `research_done` | `in` | `done` *(later)* | Army [army] = ARMY_1; Research name [text] |
| Every N seconds | `repeat_timer` | `in` | `tick` *(later)* | Every (seconds) [number] = 60; Stop after (0 = never) [number] = 0 |
| Some of a group killed | `group_partly_killed` | `in` | `killed` *(later)* | Army [army]; Group [group]; Units that must die [number] = 1 |

## Objectives (11)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Objective: kill group | `objective_kill` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Destroy the enemy; Description [text]; Army [army]; Group [group] |
| Objective: protect group | `objective_protect` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Protect the base; Description [text]; Army [army]; Group [group]; Hold for (seconds) [number] = 300 |
| Objective: capture group | `objective_capture` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Capture the structures; Description [text]; Army [army]; Group [group] |
| Objective: units in area | `objective_area` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Reach the area; Description [text]; Area [area]; Army whose units count [army] = ARMY_1; Categories [categories] = categories.ALLUNITS; Compare [choice] {>=/<=/>/</==} = >=; Count [number] = 1 |
| Objective: timer | `objective_timer` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Survive; Description [text]; Seconds [number] = 300; When it runs out [choice] {complete/failed} = complete |
| Resolve objective | `objective_resolve` | `in` | `out` | Objective node (#) [number] = 1; Result [choice] {complete/failed} = complete |
| Objective: kill or capture group | `objective_kill_or_capture` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Take out the enemy; Description [text]; Army [army]; Group [group] |
| Objective: manual (Resolve objective ends it) | `objective_basic` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Complete the task; Description [text]; Icon [choice] {kill/capture/build/protect/timer/move/reclaim/repair/locate/group} = locate |
| Objective: army statistic | `objective_army_stat` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Build up; Description [text]; Army [army] = ARMY_1; Icon [choice] {kill/capture/build/protect/timer/move/reclaim/repair/locate/group} = build; Statistic [choice] {Units_Active/Units_Killed/Units_History/Enemies_Killed} = Units_Active; Compare [choice] {>=/<=/>/</==} = >=; Value [number] = 1; Unit categories [categories] = categories.ALLUNITS |
| Objective: research | `objective_research` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Learn the technology; Description [text]; Army [army] = ARMY_1; Research names (comma separated) [namelist]; How many (0 = all) [number] = 0 |
| Objective: look at markers | `objective_camera` | `in` | `completed`, `failed` *(later)* | Kind [choice] {primary/secondary} = primary; Title [text] = Look around; Description [text]; Markers (comma separated) [markerlist] |

## Units (31)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Create unit | `create_unit` | `in` | `out` | Army [army]; Unit [unit] |
| Spawn group as platoon | `spawn_platoon` | `in` | `out` | Army [army]; Group [group]; Formation [choice] {AttackFormation/GrowthFormation/NoFormation} = AttackFormation |
| Platoon order along a chain | `platoon_order` | `in` | `out` | Army [army]; Group [group]; Order [choice] {patrol/move/attack} = patrol; Chain [chain] |
| Group patrols a chain in random order | `group_random_patrol` | `in` | `out` | Army [army]; Group [group]; Chain [chain] |
| Group goes to a marker | `group_move_marker` | `in` | `out` | Army [army]; Group [group]; Marker [marker]; Order [choice] {move/attack-move/patrol} = move; Cancel its earlier orders first [bool] = true |
| Group attacks a unit | `group_attack_unit` | `in` | `out` | Army [army]; Group [group]; Target's army [army]; Target unit [unit] |
| Group guards a unit | `group_guard_unit` | `in` | `out` | Army [army]; Group [group]; Guarded unit's army [army]; Guarded unit [unit] |
| Stop a group | `group_stop` | `in` | `out` | Army [army]; Group [group]; How [choice] {clear/stop} = clear |
| Unit goes to a marker | `unit_move_marker` | `in` | `out` | Army [army]; Unit [unit]; Marker [marker]; Order [choice] {move/attack-move/patrol} = move; Cancel its earlier orders first [bool] = true |
| Protect or kill group | `group_state` | `in` | `out` | Army [army]; Group [group]; Action [choice] {protect/unprotect/restrict-death/allow-death/kill/destroy} = protect |
| Protect or kill unit | `unit_state` | `in` | `out` | Army [army]; Unit [unit]; Action [choice] {protect/unprotect/restrict-death/allow-death/kill/destroy} = protect |
| Set group veterancy | `group_veterancy` | `in` | `out` | Army [army]; Group [group]; Level (0 to 5) [number] = 1 |
| Set unit veterancy | `unit_veterancy` | `in` | `out` | Army [army]; Unit [unit]; Level (0 to 5) [number] = 1 |
| Give group to army | `group_give` | `in` | `out` | Army [army]; Group [group]; New owner [army] |
| Give unit to army | `unit_give` | `in` | `out` | Army [army]; Unit [unit]; New owner [army] |
| Name a unit | `unit_name` | `in` | `out` | Army [army]; Unit [unit]; Name [text] |
| Unit: what can happen to it | `unit_flags` | `in` | `out` | Army [army]; Unit [unit]; Can be killed [bool] = true; Enemies can target it [bool] = true; Can be captured [bool] = true; Can be reclaimed [bool] = true; The player can select it [bool] = true; Takes damage [bool] = true |
| Group: what can happen to it | `group_flags` | `in` | `out` | Army [army]; Group [group]; Can be killed [bool] = true; Enemies can target it [bool] = true; Can be captured [bool] = true; Can be reclaimed [bool] = true; The player can select it [bool] = true; Takes damage [bool] = true |
| Set a unit's health | `unit_max_health` | `in` | `out` | Army [army]; Unit [unit]; Maximum health [number] = 5000; Heal it to full [bool] = true |
| Set a unit's speed and build rate | `unit_tuning` | `in` | `out` | Army [army]; Unit [unit]; Speed multiplier (0 = unchanged) [number] = 1; Build rate (0 = unchanged) [number] = 0 |
| Switch a weapon on or off | `unit_weapon` | `in` | `out` | Army [army]; Unit [unit]; Weapon label [text]; Enabled [bool] = true |
| Load nuke missiles | `unit_nuke_ammo` | `in` | `out` | Army [army]; Unit [unit]; Missiles [number] = 1 |
| Take an order away from a unit | `unit_command_cap` | `in` | `out` | Army [army]; Unit [unit]; Order [choice] {RULEUCC_Reclaim/RULEUCC_Capture/RULEUCC_Attack/RULEUCC_Move/RULEUCC_Patrol/RULEUCC_Guard/RULEUCC_Stop} = RULEUCC_Reclaim |
| Fly a group in by transport | `transport_deploy` | `in` | `out` | Army [army]; Group to carry [group]; Transport unit [text]; Approach chain (optional) [chain, optional]; Drop-off marker [marker]; Fly-away marker (optional) [marker, optional]; Platoon moves to (optional) [marker, optional]; Keep the transport after it leaves [bool] = false |
| Visual effect at a marker | `create_effect` | `in` | `out` | Effect blueprint [text]; Marker [marker] |
| Warp a unit to a marker | `unit_warp` | `in` | `out` | Army [army]; Unit [unit]; Marker [marker] |
| Switch a unit's shield on or off | `unit_shield` | `in` | `out` | Army [army]; Unit [unit]; Shield on [bool] = true |
| Launch a nuke from a unit | `unit_launch_nuke` | `in` | `out` | Army [army]; Launcher [unit]; Target marker [marker] |
| Start the AI's anti-nuke defence | `anti_nuke` | `in` | `out` | - |
| Activate a drone launcher | `drone_launcher` | `in` | `out` | Army [army]; Launcher [unit]; Most drones at once [number] = 4; Drones patrol this chain [chain]; Respawn delay (seconds) [number] = 10; First launch after (seconds) [number] = 5 |
| Start a Krakken's behaviour | `krakken_ai` | `in` | `out` | Army [army]; Krakken [unit]; Patrol chain [chain]; Home marker [marker]; Home area [area]; Restart delay (seconds) [number] = 30 |

## Actions (8)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Spawn group | `spawn_group` | `in` | `out` | Army [army]; Group [group] |
| Give order | `give_order` | `in` | `out` | Army [army]; Group [group]; Order [choice] {patrol/move/attack-move} = patrol; Chain [chain] |
| Dialogue | `dialogue` | `in` | `done` *(later)* | Speaker [text]; Text [text]; Shown for (seconds) [number] = 5; Video file (optional) [text]; Voice bank (optional) [text]; Voice cue (optional) [text] |
| Show text | `show_text` | `in` | `out` | Text [text]; For (seconds) [number] = 5; Where [choice] {center/leftbottom} = center |
| Write to the game log | `log_message` | `in` | `out` | Text [text] |
| Stop the dialogue queue | `flush_dialogue` | `in` | `out` | - |
| Play a music event | `play_music` | `in` | `out` | Music event handle [text] |
| Play a sound | `play_sound` | `in` | `out` | Sound bank [text]; Cue [text] |

## World (10)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Damage units around a marker | `damage_area` | `in` | `out` | Marker [marker]; Radius [number] = 30; Spare commanders [bool] = true; Damage to the player (army 1) [number] = 500; Damage to the others [number] = 500 |
| Kill units around a marker | `kill_area` | `in` | `out` | Marker [marker]; Radius [number] = 30; Spare commanders [bool] = true |
| Nuke blast at a marker | `nuke_effect` | `in` | `out` | Marker [marker]; Random range [number] = 10; Leave a crater [bool] = true |
| Set playable area | `set_playable_area` | `in` | `out` | Area [area] |
| Reveal an area | `reveal_area` | `in` | `out` | Army that sees [army] = ARMY_1; Marker [marker]; Radius [number] = 60; For (seconds) [number] = 30 |
| Give intel at a marker | `create_intel` | `in` | `out` | Army that gets it [army] = ARMY_1; Marker [marker]; Radius [number] = 100; Intel [choice] {Vision/Radar/Sonar/Omni} = Radar; For (seconds, 0 = forever) [number] = 0 |
| Take away the intel at a marker | `clear_intel` | `in` | `out` | Marker [marker]; Radius [number] = 100 |
| Set relation of two armies | `set_alliance` | `in` | `out` | Army [army]; Other army [army]; Relation [choice] {Ally/Enemy/Neutral} = Ally |
| Block building along a chain | `block_build_grid` | `in` | `out` | Army [army]; Chain [chain]; Large blockers [bool] = false |
| Create weather | `weather` | `in` | `out` | Map style [text] = Tundra; Weather effect [text] = WhitePatchyClouds03 |

## Economy (12)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Give resources | `give_resources` | `in` | `out` | Army [army] = ARMY_1; Mass [number] = 1000; Energy [number] = 1000 |
| Take resources away | `take_resources` | `in` | `out` | Army [army] = ARMY_1; Mass [number] = 1000; Energy [number] = 1000 |
| Set unit cap | `set_unit_cap` | `in` | `out` | Army [army] = ARMY_1; Unit cap [number] = 1000 |
| Restrict or allow units | `build_restriction` | `in` | `out` | Army [army] = ARMY_1; Unit categories [categories] = categories.EXPERIMENTAL; Mode [choice] {restrict/allow} = allow |
| Award research points | `award_research` | `in` | `out` | Army [army] = ARMY_1; Points [number] = 5; After (seconds) [number] = 0; Show it as a tech award [bool] = false |
| Complete research | `complete_research` | `in` | `out` | Army [army] = ARMY_1; Research names (comma separated) [namelist] |
| Lock or unlock research | `research_restrict` | `in` | `out` | Army [army] = ARMY_1; Research names (comma separated) [namelist]; Lock (off: unlock) [bool] = true |
| Count an army's units in an area | `count_in_area` | `in` | `out` | Army [army] = ARMY_1; Area [area]; Unit categories [categories] = categories.ALLUNITS; Store in variable [variable] |
| Count an army's units | `count_army_units` | `in` | `out` | Army [army] = ARMY_1; Unit categories [categories] = categories.ALLUNITS; Store in variable [variable] |
| Random number | `random_number` | `in` | `out` | From [number] = 1; To [number] = 10; Store in variable [variable] |
| Read an army's stored resources | `count_economy` | `in` | `out` | Army [army] = ARMY_1; Resource [choice] {MASS/ENERGY} = MASS; Store in variable [variable] |
| Has an army learned a research? | `check_research` | `in` | `out` | Army [army] = ARMY_1; Research name [text]; Store the answer (a true/false variable) [variable] |

## Cutscene (NIS) (39)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Cutscene: begin | `nis_begin_standard` | `in` | `out` | Start field of view (0 = default) [number] = 0; Black bars [bool] = true; Lock the player's input [bool] = true |
| Cutscene: begin the opening | `nis_begin_opening` | `in` | `out` | Camera marker [marker]; Zoom [number] = 40; Start position marker (optional) [marker, optional]; Field of view (0 = default) [number] = 0 |
| Cutscene: begin in the middle of the mission | `nis_begin_midop` | `in` | `out` | Area to show [area]; Minimum zoom (0 = default) [number] = 0; Field of view (0 = default) [number] = 0 |
| Cutscene: begin the victory scene | `nis_begin_victory` | `in` | `out` | Start field of view (0 = default) [number] = 0; Black bars [bool] = true; Lock the player's input [bool] = true |
| Cutscene: end | `nis_end_standard` | `in` | `done` *(later)* | Zoom to restore (0 = as it was) [number] = 0; Forget the intel it gave [bool] = false; Restore over (seconds) [number] = 2 |
| Cutscene: end the opening | `nis_end_opening` | `in` | `done` *(later)* | Zoom to restore (0 = as it was) [number] = 100; Forget the intel it gave [bool] = false; Restore over (seconds) [number] = 2 |
| Cutscene: end the mid-mission scene | `nis_end_midop` | `in` | `done` *(later)* | Forget the intel it gave [bool] = false; Restore over (seconds) [number] = 2 |
| Cutscene: end without restoring the camera | `nis_end_norestore` | `in` | `done` *(later)* | Forget the intel it gave [bool] = false; Over (seconds) [number] = 0 |
| Cutscene: end and win | `nis_end_victory` | `in` | none (ends the chain) | - |
| Cutscene: camera to a marker | `nis_zoom_to` | `in` | `done` *(later)* | Camera marker [marker]; Distance (negative = closer) [number, signed] = 0; Seconds [number] = 5; Wait for the move [choice] {wait/go on at once} = wait |
| Cutscene: camera moves to a marker | `nis_move_to` | `in` | `done` *(later)* | Camera marker [marker]; Seconds [number] = 5; Wait for the move [choice] {wait/go on at once} = wait |
| Cutscene: camera jumps to a marker | `nis_snap_to` | `in` | `out` | Camera marker [marker] |
| Cutscene: change the zoom | `nis_set_zoom` | `in` | `out` | Distance [number] = 60; Over (seconds) [number] = 3 |
| Cutscene: change the field of view | `nis_set_fov` | `in` | `out` | Field of view [number] = 55; Over (seconds) [number] = 0 |
| Cutscene: spin the camera | `nis_spin` | `in` | `out` | Degrees per second [number, signed] = 10; For (seconds) [number] = 5 |
| Cutscene: camera follows a unit | `nis_track_unit` | `in` | `out` | Army [army]; Unit [unit]; Heading (180 = the unit's front) [number, signed] = 135; Pitch (90 = from above) [number, signed] = 35; Distance [number] = 60; Time to get there [number] = 2; Offset X [number, signed] = 0; Offset Y [number, signed] = 0; Offset Z [number, signed] = 0 |
| Cutscene: camera follows a group | `nis_track_group` | `in` | `out` | Army [army]; Group [group]; Zoom [number] = 60; Time to get there [number] = 2 |
| Cutscene: fade the screen | `nis_fade` | `in` | `done` *(later)* | Fade [choice] {out/in/out to white/in from white} = out; Seconds [number] = 2 |
| Cutscene: dialogue | `nis_dialog` | `in` | `done` *(later)* | Speaker [text]; Text [text]; Shown for (seconds) [number] = 5; Video file (optional) [text]; Voice bank (optional) [text]; Voice cue (optional) [text]; Wait for the line [choice] {wait/go on at once} = wait |
| Cutscene: show the mission title | `nis_title` | `in` | `out` | Title [text] |
| Cutscene: show place and date | `nis_location` | `in` | `out` | Place [text]; Date [text] |
| Cutscene: reveal around a marker | `nis_viz_marker` | `in` | `out` | Marker [marker]; Radius [number] = 100; For (seconds, 0 = until the end) [number] = 0 |
| Cutscene: arrow at a marker | `nis_arrow` | `in` | `out` | Marker [marker]; Height (0 = default) [number] = 0 |
| Cutscene: show or hide a unit | `nis_unit_visible` | `in` | `out` | Army [army]; Unit [unit]; Mode [choice] {show/hide} = hide |
| Cutscene: show or hide a group | `nis_group_visible` | `in` | `out` | Army [army]; Group [group]; Mode [choice] {show/hide} = hide |
| Cutscene: move a unit to a marker at once | `nis_warp_unit` | `in` | `out` | Army [army]; Unit [unit]; Marker [marker] |
| Cutscene: move a group to a marker at once | `nis_warp_group` | `in` | `out` | Army [army]; Group [group]; Marker [marker] |
| Cutscene: a unit that exists only in it | `nis_create_unit` | `in` | `out` | Army [army]; Unit [unit] |
| Cutscene: a group that exists only in it | `nis_create_group` | `in` | `out` | Army [army]; Group [group] |
| Cutscene: a commander climbs out | `nis_unhunker` | `in` | `out` | Army [army]; Unit [unit] |
| Cutscene: stop the fighting | `nis_no_combat` | `in` | `out` | - |
| Cutscene: play a movie | `nis_movie` | `in` | `done` *(later)* | Movie path [text] = /movies/FMV_CYB_OPENING |
| Cutscene: a transport brings a group | `nis_transport_arrival` | `in` | `done` *(later)* | Army [army]; Group to carry [group]; Transport unit [text]; Approach chain (optional) [chain, optional]; Drop-off marker [marker]; Fly-away marker (optional) [marker, optional]; Platoon moves to (optional) [marker, optional]; Keep the transport after it leaves [bool] = false; The player cannot use the transport after the scene [bool] = true |
| Cutscene: camera circles a unit | `nis_spin_around` | `in` | `out` | Army [army]; Unit [unit]; Start heading [number, signed] = 135; Pitch [number, signed] = 30; Rotations per second [number] = 0.03; Distance [number] = 50; For (seconds) [number] = 10 |
| Cutscene: camera on a unit's nose | `nis_nose_cam` | `in` | `out` | Army [army]; Unit [unit]; Pitch adjustment [number, signed] = 0; Zoom [number] = 10; For (seconds) [number] = 5; Time to get there [number] = 1 |
| Cutscene: stop following | `nis_clear_tracking` | `in` | `out` | Zoom [number] = 60; Over (seconds) [number] = 2 |
| Cutscene: group follows a chain | `nis_group_move_chain` | `in` | `done` *(later)* | Army [army]; Group [group]; Chain [chain] |
| Cutscene: hide a unit and move it | `nis_hide_warp` | `in` | `out` | Army [army]; Unit [unit]; Marker [marker] |
| Cutscene: smooth camera starts and stops | `nis_ease` | `in` | `out` | Camera moves [choice] {smooth/linear} = smooth |

## Cutscene (simple) (3)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Simple cutscene: begin | `cutscene_start` | `in` | `out` | - |
| Simple cutscene: camera move | `camera_move` | `in` | `done` *(later)* | Marker [marker]; Seconds [number] = 3 |
| Simple cutscene: end | `cutscene_end` | `in` | `out` | - |

## Bases (8)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Start base | `base_start` | `in` | `out` | Base [base] |
| Enable / disable OpAI | `opai_set` | `in` | `out` | Base [base]; OpAI [opai]; State [choice] {enable/disable} = enable |
| Make a base an expansion of another | `base_expansion` | `in` | `out` | Base [base]; Expansion base [base] |
| Switch a base on or off | `base_active` | `in` | `out` | Base [base]; Active [bool] = true |
| Set a base's engineer count | `base_engineers` | `in` | `out` | Base [base]; Engineers [number] = 4 |
| Let a base build every structure | `base_build_all` | `in` | `out` | Base [base]; Build everything [bool] = true |
| Choose the army an OpAI attacks | `opai_target` | `in` | `out` | Base [base]; OpAI [opai]; Army to attack [army] = ARMY_1 |
| Change an OpAI's wave size and pace | `opai_tune` | `in` | `out` | Base [base]; OpAI [opai]; Child count (0 = unchanged) [number] = 0; Max platoons (0 = unchanged) [number] = 0; Delay (seconds) [number] = 0 |

## Flow (16)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Set variable | `set_variable` | `in` | `out` | Variable [variable]; Operation [choice] {set/add} = set; Value [text] = 1 |
| Branch on variable | `branch` | `in` | `true`, `false` | Variable [variable]; Compare [choice] {==/~=/>/>=/</<=} = ==; Value [text] = 1 |
| Advance to stage | `advance_stage` | `in` | none (ends the chain) | Stage [stage] |
| Victory | `victory` | `in` | none (ends the chain) | - |
| Defeat | `defeat` | `in` | none (ends the chain) | - |
| Defeat when this unit dies | `defeat_when_unit_dies` | `in` | `out` | Army [army]; Unit [unit] |
| Only the first time | `once` | `in` | `out` | - |
| Wait for all inputs | `join` | 1..N (`count`) | `out` | Inputs (2 to 12) [number] = 2; Can fire again [bool] = false |
| Sequence (every output, one after the other) | `sequence` | `in` | 1..N (`count`) | Outputs (2 to 12) [number] = 3 |
| Sequence with pauses | `sequence_timed` | `in` | 1..N (`count`) *(later)* | Outputs (2 to 12) [number] = 3; Pause between outputs (seconds) [number] = 1 |
| Switch on a variable | `switch_variable` | `in` | 1..N (`cases`), `other` | Store in variable [variable]; Values (comma separated, one output each) [namelist] = 1,2,3 |
| Gate (lets the flow through while it is open) | `gate` | `pass`, `open`, `close` | `out`, `blocked` | Starts open [bool] = true |
| Counter | `counter` | `count`, `reset` | `each`, `reached` | Reaches at [number] = 3; Store the count in variable [variable, optional] |
| Ask the player for the difficulty | `ask_difficulty` | `in` | `chosen` *(later)* | Question [text] = Choose the difficulty; First button [text] = Easy; Second button [text] = Normal; Third button [text] = Hard; Where the menu is [choice] {center/left/right} = center; Only player 1 can answer [bool] = true; Used when nobody answers [choice] {easy/normal/hard} = normal; Wait for an answer (seconds, 0 = for ever) [number] = 0; Store the answer (1, 2 or 3) in variable [variable, optional] |
| Branch: cutscenes allowed? | `cutscenes_allowed` | `in` | `play`, `skip` | - |
| Random branch | `random_branch` | `in` | `yes`, `no` | Chance of yes (percent) [number] = 50 |

## Loops (14)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Repeat N times | `loop_repeat` | `start`, `next`, `break` | `body`, `done`, `stopped` *(later)* | Rounds [number] = 3; Next round by itself [bool] = false; Pause between rounds (seconds) [number] = 0; Store the round number (1, 2...) in variable [variable, optional] |
| Repeat while a variable matches | `loop_while` | `start`, `next`, `break` | `body`, `done`, `stopped` *(later)* | Store in variable [variable]; Compare [choice] {==/~=/>/>=/</<=} = ==; Value [text] = 1; Next round by itself [bool] = false; Pause between rounds (seconds) [number] = 0.5; Most rounds (a safety stop) [number] = 1000; Store the round number (1, 2...) in variable [variable, optional] |
| Repeat every N seconds until stopped | `loop_forever` | `start`, `stop`, `pause`, `resume` | `tick`, `stopped` *(later)* | Seconds between ticks (at least 0.1) [number] = 5; First tick right away [bool] = false; Store the tick number (1, 2...) in variable [variable, optional] |
| For each unit in a group | `loop_foreach` | `start`, `next`, `break` | `each`, `done`, `stopped` *(later)* | Army [army]; Group [group]; Next round by itself [bool] = false; Pause between units (seconds) [number] = 0; Store the number of the unit (1, 2...) in variable [variable, optional] |
| Loop unit: kill it | `loop_unit_kill` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number] |
| Loop unit: remove it | `loop_unit_remove` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number] |
| Loop unit: stop its orders | `loop_unit_stop` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number] |
| Loop unit: move to a marker | `loop_unit_move` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; Marker [marker] |
| Loop unit: attack-move to a marker | `loop_unit_attack_move` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; Marker [marker] |
| Loop unit: patrol a chain | `loop_unit_patrol` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; Chain [chain] |
| Loop unit: give it to an army | `loop_unit_give` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; New owner [army] |
| Loop unit: set its veterancy | `loop_unit_veterancy` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; Level (0 to 5) [number] = 1 |
| Loop unit: set its health (percent) | `loop_unit_health` | `in` | `out` | Loop node number (the # of the 'For each unit' node) [number]; Percent of full health [number] = 100 |
| Loop unit: is it of a kind? | `loop_unit_is` | `in` | `yes`, `no` | Loop node number (the # of the 'For each unit' node) [number]; Unit categories [categories] = categories.ALLUNITS |

## Coop (1)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Branch: solo or co-op | `coop_branch` | `in` | `solo`, `coop` | - |

## Raw Lua (1)

| Node | Type | Inputs | Outputs | Parameters |
|---|---|---|---|---|
| Raw Lua | `raw_lua` | `in` | `out` | Lua [code] |

