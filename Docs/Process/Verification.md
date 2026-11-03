# Verification

How to check a change beyond what the unit tests cover, and how to report what was checked. The tests themselves are in
[Testing](Testing.md); how to edit safely is in [Editing](Editing.md). Part of the documentation plan (`Docs/DocumentationPlan.md`,
section 5).

## 1. Say what the evidence is

Every report of a check, in a commit or elsewhere, names three things: what was verified, what was **not** verified, and the evidence
for each. The levels, from weakest to strongest:

1. **Code reading**: the path was read and it looks right. It proves nothing about behaviour.
2. **Unit test**: standalone xUnit or an EditMode test, run.
3. **Headless run in the Unity editor**: a scenario run in batch mode, which reads the state afterwards.
4. **Player run**: the same scenario in a standalone build, which reads its own report.
5. **Manual check in the built editor**, by a person.

"Works" without a level is not a report. A statement about the game itself (what the game loads, how a map plays) is level 5 or it is
unverified; the editor never runs the game.

## 2. The order of checks for a change

1. Compile the editor (open the project, or run a batch build) and compile a **player build**: the `#if UNITY_EDITOR` branches differ
   and an error in the other branch shows only in the other build ([Editing](Editing.md), section 3).
2. Standalone tests: `dotnet test` ([Testing](Testing.md)). Compare with the known baseline.
3. EditMode tests.
4. The scenario of the touched system, when it needs one (below).
5. Look at the result: a screenshot taken by the player, or the exported files read back.
6. Last, a manual pass in the built editor.

What exists today: step 2 and 3 (commands in [Testing](Testing.md)), a player build from Unity's Build Settings, and the technique in
section 3. What is planned and not yet in the repository: one script that runs all of it, and the scenario harness
(`Docs/DocumentationPlan.md`, sections 5.5 and 5.6). Until then a scenario is a script written for the occasion in a scratch Unity
project, following section 3. The scratch project used so far is described in [ScratchVerifyProject](ScratchVerifyProject.md).

## 3. Scenarios: the technique

These are the working rules of the scenarios used so far. They are written down because each one cost a failed run.

### Headless in the editor

- A scenario is a static method run by `Unity.exe -batchmode -projectPath <project> -executeMethod <Class>.<Method> -quit -logFile <log>`.
  It logs lines with a tag and the output is searched for that tag. A scratch project holding a copy of `Assets/SC2` works; the project
  must not be open in another Unity.
- A copy of `Assets/SC2` goes stale. **Refresh it from the repository before a run**, otherwise the scenario tests old code. Say in the
  report which commit was in the copy.
- In batch mode `EditorUtility.DisplayDialog` answers "cancel". Call the method behind the dialog instead of going through it, and test the
  cancel path separately.
- A hand-made `UnityEngine.Event` cannot take the type `MouseDown` outside `OnGUI`. Deliver events through a real `EditorWindow` with
  `SendEvent`, from a window whose `OnGUI` calls the code under test. The first events after the handler changes only lay the window
  out; send a harmless warm-up event first, and keep clicks away from controls that would consume them.
- The live runtime objects of a shell (`SC2TerrainShell.Runtime` and the like) are created lazily by `Update()` and `OnGameViewGUI`: call
  `Update()` a few times and test for null before using them.
- A preview that depends on a generation counter (the baked lighting preview, for example) refreshes when the shell applies the preview
  mode; a scenario that changes the data directly has to trigger that refresh the way the UI does.
- Textures: a material with no diffuse texture shows one grey for both materials, so blending between them is invisible. Give the
  materials contrasting textures when a scenario renders blending.

### Player run

- Build a player and start it with a command line flag. A small scratch `MonoBehaviour` with `[RuntimeInitializeOnLoadMethod]` looks for the
  flag, runs the scenario, writes a **report file**, and calls `Application.Quit()`. Read the report file; do not rely on the console.
- A screenshot comes from the player itself: `ScreenCapture.CaptureScreenshot` after `WaitForEndOfFrame`. Never a screenshot of the desktop.
- Check that the build you run is newer than the change: compare the date of the `.exe` with the date of the edit. A player built before
  the change shows the old behaviour, and reporting it as the new one is a false result.

### Isolation: never touch a user's data

The project settings name the company `BDFTM` and the product `SupCom2 map editor`, so a player built from this project reads and writes
`%USERPROFILE%\AppData\LocalLow\BDFTM\SupCom2 map editor\` (maps, backups, and through `PlayerPrefs` the settings of whoever uses the
build). A scenario that saves, deletes or restores maps, or changes a setting, must not run from such a build. Build the scenario from a
scratch project with another company and product name (its data then goes to its own folder), and give test maps a name no real map has.
A save folder override (a command line argument or an environment variable read by `SC2SaveStorage`) is planned work that would make this
safe from the repository's own project; it does not exist yet.

## 4. Checking an export

The exporter is the point where an editor mistake becomes a map the game refuses to load. After a change that can reach it:

1. Export a map in a scenario, with the unusual inputs the change concerns (a marker path, a drive path, a missing texture, an empty slot).
2. Read the written files back with the parsers (`TerrainBdf.Parse(BdfContainer.Read(bytes, TerrainBdf.Padding))`, `InfoBdf.Parse(...)` and
   the like) and compare with what was meant.
3. Scan **every** file of the output as text for the editor markers `archive:` and `file:` and for drive letters. Any hit is a leak.
4. State in the report that this was not played in the game.

## 5. Checking the interface

- Drive the state with real events (above), then read the state: the data changed, the confirm opened, the selection moved.
- For a look at the layout, take a player screenshot at more than one UI scale (the layout was broken at higher scales before) and read it.
- A rule behind the interface has its own unit test ([Testing](Testing.md)); the scenario shows that the interface calls it.

## 6. The report

Put this in the commit message or the pull request, in a few lines:

```
Verified: <what, at which level>
Not verified: <what, and why>
Evidence: <the test names, the scenario, the files read back, the screenshot>
```

If the build was not rebuilt or the game was not started, the line "Not verified" says so.
