# The scratch verification project

What `C:\temp\SC2UnityVerify45` is, what is in it, and how to run it. It is the Unity project in which every headless scenario, player
build check and screenshot of this editor has been run so far. The technique and the rules for reporting are in
[Verification](Verification.md); this page describes the project itself.

**It is not part of the repository.** It lives at `C:\temp\SC2UnityVerify45` on one machine, it has no git history, and nobody else can
open it. [DocumentationPlan](../DocumentationPlan.md) (sections 5.5 and 5.6) plans to move the scenarios into the repository; until
then this page is the only description of the setup.

## 1. What it is

A throwaway Unity project (Unity `6000.0.77f1`, the version the package is built with) that holds a **copy** of `Assets/SC2` plus a folder
of scenarios (`Assets/VerifyBuildScript`) that call into that copy. It exists because:

- a batch run needs a project with its own settings, package set and import cache, and a build needs a scene; a throwaway project
  supplies them without changing the repository's own project settings;
- a scenario must be free to write files, save maps, change settings and be built as a player, none of which may touch the repository or
  the data of the person who uses the editor;
- several people or sessions edit the repository at once, so running against a copy means a scenario sees one known state.

It is **a copy, not a link**: `Assets/SC2` is a real folder, so it goes stale. Refresh it before every run (section 4).

## 2. Layout

```
C:\temp\SC2UnityVerify45
  Assets
    SC2                  copy of the repository's Assets/SC2 (Scripts, Runtime, Editor, Resources, Shaders, Tests~, Tools~)
    VerifyBuildScript
      Editor             scenarios run with -executeMethod (see section 3)
      Runtime            scenarios compiled into a player, started with a command line flag
    SC2Textures, StreamingAssets/SC2Textures, Resources   texture data the editor loads at run time (about 95 MB in total)
    Editor, MobileDependencyResolver                     leftovers of the template, not used by scenarios
  Packages               manifest.json, packages-lock.json (the package set the editor needs)
  ProjectSettings        ProjectVersion.txt (6000.0.77f1), player settings
  Library                Unity's import cache, about 500 MB; deleting it forces a full reimport on the next open
  Logs                   Unity's own logs of earlier batch runs
  build_*.txt, *.csproj  logs and generated files of earlier runs; safe to ignore
```

- **Player settings:** company `DefaultCompany`, product `SupCom2 map editor`. A player built here therefore writes to
  `%USERPROFILE%\AppData\LocalLow\DefaultCompany\SupCom2 map editor\`, **not** to the real `BDFTM` folder. This is the isolation rule of
  [Verification](Verification.md), section 3, and the reason the project exists. Do not change the company name in this project.
- `Tests~` and `Tools~` are copied with the rest but are not compiled (the trailing `~` hides them from Unity). The folder
  `Assets/SC2/Tests` (Unity EditMode tests) is **not** in the copy today; see section 6.

## 3. The scenarios

All are `static` methods, "scratch-only" by their own header comments, and none is part of `Assets/SC2`. A scenario logs lines with a tag
(for example `RENAMECHECK`, `[StampShot]`) and leaves files under `C:\temp`. Read the log by searching for the tag.

| File | Method(s) | What it checks | Output |
|---|---|---|---|
| `SC2VerifyStandaloneBuild` | `Run` | Compiles a **Windows player** (catches `#if UNITY_EDITOR` divergences). Logs `SC2VERIFY_RESULT=`, `SC2VERIFY_ERRORS=`, `SC2VERIFY_WARNINGS=` | player in `C:\temp\SC2VerifyBuildOutput\SC2VerifyBuild.exe` |
| `SC2ImportRenameCheck` | `Run` | Imports an archived map into a map with another name, exports it, and reads back the scenario paths, the texture folder and the terrain slots. Inputs by environment variable: `SC2CHECK_SCD` (default `C:\Users\user\Desktop\eclipse.scd`), `SC2CHECK_NAME`, `SC2CHECK_OUT` | `C:\temp\SC2ImportRenameExport` |
| `SC2WaterShot` | about 40 methods (`Run`, `RunSectors`, `ShoreTest`, `ExportPathLeakTest`, `SharedMaterialsTest`, `PathGridTest`, `UpdateNoticeTest`, `BgMaskExportTest`, ...) | Renders the water shader over a test shore; many others are state checks that grew in the same file (layers, twins, hotkeys, export directory, army and chain renames). `SHOT_TAG` names the output files | PNGs in `C:\temp\water_shot`, some in `C:\temp\bakeblend` |
| `SC2MaskShot` | `Run` | The real terrain runtime over a flat plane with a mask in the Textured and RNM views | `C:\temp\maskshots` |
| `SC2StampShot`, `SC2NormalShot`, `SC2EffectShot` | `Run` | Brush stamps, normal-map display, effect preview, as images | `C:\temp\stampshots` |
| `SC2BgSmoke`, `SC2SeamSmoke`, `SC2ViewStateSmoke`, `SC2SimplifiedTabSmoke`, `SC2PerfSmoke`, `SC2ThreadSmoke` | `Run` | The Background tab: grid density, seam between core and background, view modes per material, tab switching, per-frame cost, the heavy halves of the background jobs on a worker thread | log only (`SC2BgSmokeExport` for exports) |
| `SC2ShadowRepro`, `SC2ShadowField` | `Execute`, `Sweep`, `Quick`, `ImportedMask` | Lighting bake with and without an imported frame object under the shadow rules | log |
| `SC2TexExportShot`, `SC2MapshareShot` | `Run` | Where textures picked from the game archive are written on export; a texture this machine lacks served from the map share | `C:\temp\SC2TexExport` |
| `SC2CatalogProbe`, `SC2CenterProbe` | `Run` | Mission node catalog sanity; symmetry centre of a vanilla map | log |
| `Runtime/HotkeyShot`, `TexShot`, `SymShot`, `RestoreShot` | start the player with `-hotkeyshot`, `-texshot`, `-symshot`, `-restoreshot` | The Hotkeys panel, the Texture Browser, the Symmetry menu and "Restore Backup..." in a **player**, at several UI scales, using the player's own `ScreenCapture` | PNGs and a log named on the command line |

The table comes from the headers and entry points of the files; it was not re-run for this page. Only `SC2ImportRenameCheck` and
`SC2VerifyStandaloneBuild` were run in the last session.

Several editor scenarios set `SC2GameInstallSettings.RootPath` to `D:\SteamLibrary\steamapps\common\Supreme Commander 2\gamedata`. On
another machine change it (the scenarios read the game's archives from there), and the map-import scenarios need an archive on disk.

## 4. Refreshing the copy

Run this before a scenario, otherwise it tests old code. From the repository root (PowerShell):

```powershell
$repo = "C:\Users\user\SupCom2 map editor\Assets\SC2"
$h    = "C:\temp\SC2UnityVerify45\Assets\SC2"
foreach ($d in "Scripts","Runtime","Editor","Resources","Shaders") {
    robocopy "$repo\$d" "$h\$d" /E /XF "*.meta" /NFL /NDL /NJH /NJS /NP | Out-Null
}
```

- Copy with `robocopy` (or `cp`), **not** by piping file text through PowerShell: `Get-Content | Set-Content` and `-NoNewline` glue or
  re-encode lines and the project then fails to compile.
- `/XF "*.meta"` leaves the project's own `.meta` files (and guids) alone; Unity creates a `.meta` for a new file by itself.
- Use `/E`, not `/MIR`, unless a file was deleted or renamed in the repository: `/MIR` also deletes the project's own files in that folder.
- Say in a report which commit (or which uncommitted files) the copy held. Files that other work streams have changed but not committed
  are copied too.
- The copy carries no mission-panel harness patch right now (section 6); `Scripts` should equal the repository's.

## 5. Running

Unity is at `C:\Program Files\Unity\Hub\Editor\6000.0.77f1\Editor\Unity.exe`. **The project must not be open in another Unity** (a lock
stops the second start) and two batch runs on it cannot overlap.

**Compile check, editor and player** (also the check that the copy is consistent). This is the form used in earlier runs, with `-quit`:

```powershell
$log = "C:\temp\SC2VerifyBuild.log"
& "C:\Program Files\Unity\Hub\Editor\6000.0.77f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath C:\temp\SC2UnityVerify45 -executeMethod SC2VerifyStandaloneBuild.Run -logFile $log | Out-Null
Select-String -Path $log -Pattern 'error CS|SC2VERIFY_RESULT|SC2VERIFY_ERRORS|Build Finished'
```

Zero `error CS` lines and `SC2VERIFY_RESULT=Succeeded` is the expected result. A first run after a long break reimports and takes minutes.

**An editor scenario** (the scenarios exit the editor themselves in a `finally` block):

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.0.77f1\Editor\Unity.exe" -batchmode -nographics -projectPath C:\temp\SC2UnityVerify45 -executeMethod SC2ImportRenameCheck.Run -logFile C:\temp\SC2RenameCheck.log
Select-String -Path C:\temp\SC2RenameCheck.log -Pattern 'RENAMECHECK|error CS'
```

Replace the class and method with the scenario from section 3; pass inputs as environment variables set in the same PowerShell session
(`$env:SC2CHECK_NAME = "My_Test"`).

**A player scenario:** build with `SC2VerifyStandaloneBuild.Run`, then start the player with the flag and a log path, and read the files
it wrote:

```powershell
& C:\temp\SC2VerifyBuildOutput\SC2VerifyBuild.exe -hotkeyshot -screen-width 1500 -screen-height 900 -screen-fullscreen 0 -logFile C:\temp\hk_player.log
```

Compare the date of the `.exe` with the date of the change first: an old build shows old behaviour (see [Verification](Verification.md),
section 3).

## 6. Known state and traps

- **Moved aside, not deleted.** To make `ImportRenameCheck` compile, two things were moved from this project to `C:\temp\scratch_aside`:
  the old `Assets/SC2/Tests` folder (it no longer compiled against the current code) and `SC2MissionClickCheck.cs` (the mission panel harness, which
  needs the mission panel's GUI calls patched, so it cannot sit in a project whose code is plain). That harness now lives in the repository
  (`Assets/SC2/Tools~/MissionPanelHarness`) and `Invoke-MissionHarness.ps1` patches a project for the run and **restores it afterwards**; see
  [MissionPanelHarness](MissionPanelHarness.md). The old `C:\temp\SC2MissionClickCheck.cs.bak` and `mission_probe_sync*.ps1` are superseded by it. Put the
  old folder back only to run those, and expect the compile errors that made them leave.
- **EditMode tests are not run from this project today.** Its copy of `Assets/SC2/Tests` is absent; see [Testing](Testing.md) for the
  command and its status.
- **Shared project.** Other sessions use this project too. Before a run, look at `Assets/VerifyBuildScript/Editor` and `Assets/SC2` for
  changes you did not make, and do not delete files you did not create.
- **A scenario that dead-ends** reports success if it exits without checking. Read the log for the line that shows the result
  (the scenario prints it); an export that logs `Export cancelled: too many triangles.` produced nothing to inspect. The flat plane of a
  new map plus an imported map exceeds the triangle budget unless `ImportOnlyMode` is on, as `ImportRenameCheck` sets it.
- **Batch mode answers every dialog "cancel"** and has no screen: call the method behind a dialog, and use a real `EditorWindow` with
  `SendEvent` for IMGUI input ([Verification](Verification.md), section 3).
- **Main-thread queue.** Export and bake code posts work to the busy overlay's main thread queue; a scenario that waits for a task must
  pump it (`SC2BusyOverlay.Tick(null)` in a loop, as `ImportRenameCheck.Wait` does) or it waits forever.
- **Never take a screenshot of the desktop.** Images come from a `RenderTexture` or from the player's own `ScreenCapture`.
- **Test data names.** Give test maps a name no real map has, and keep outputs under `C:\temp`.

## 7. Adding a scenario

1. Create `Assets/VerifyBuildScript/Editor/<Name>.cs` with `public static void Run()`. Start the file with a comment that says what it
   checks and that it is scratch-only.
2. Wrap the body in `try / catch (Exception) { Debug.LogError("<TAG>_FAIL " + ex); } finally { EditorApplication.Exit(0); }`, so a batch run
   never hangs and a failure is greppable.
3. Print one tagged line per fact that decides the result, and print the final "DONE" line last.
4. Run it twice, once on the code before the change and once after, and keep both logs: a scenario that cannot fail on the old code
   does not show the fix.
5. Do not put it under `Assets/SC2`.

## 8. Not verified here

- The scenario table was written from the files, not by running every scenario.
- Recreating the project from nothing (a new project, the package set, the texture folders) was not tried; the contents of
  `Packages/manifest.json` and `ProjectSettings` are the record of what was needed.
- The refresh command in section 4 is the pattern of the sync scripts used so far, assembled for this page; it was not run as written.
- Whether the project works on another machine: the game path, the archive on the desktop and the Unity install path are fixed in the
  scenarios and in this page.
