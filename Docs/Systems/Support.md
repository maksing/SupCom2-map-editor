# Support: update notice, settings, logging, file manager, frame timing

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Small services that every part of the editor uses and that are not part of a map: telling the user a newer version exists, the save and autosave preferences, the log file, showing a folder in the system file manager, and a cheap per-section frame timer to find slow frames from a log.

Not included: how a map is saved and backed up ([document and persistence](DocumentAndPersistence.md)), the other per-user preferences (theme, hotkeys, UI scale, visibility, see [editor UI](EditorUi.md)), the release process ([tests and release](TestsAndRelease.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Version and update feed | `Scripts/Unified/SC2EditorVersion.cs` (`SC2EditorVersion`: `Current`, `TryParse`, `Compare`, `IsNewer`; `SC2ReleaseInfo`; `SC2UpdateFeed.PickLatestRelease`/`PickLatestTag`) |
| Update notice | `Scripts/Unified/SC2UpdateNotice.cs` (`BeginCheck`, `Tick`, `DrawOverlay`), opt-out and skipped version in `SC2CustomizationSettings` |
| Save preferences | `Scripts/Unified/SC2SaveSettings.cs` (`AutosaveEnabled`, `AutosaveIntervalMinutes`, `MaxBackupsPerMap`, `WriteLegacyJson`) |
| Log | `Scripts/TerrainEditor/SC2Log.cs` (`Log`, `LogWarning`, `LogError`, `LogException`, `Flush`, `Close`, `LogPath`) |
| File manager | `Scripts/Unified/SC2FileManager.cs` (`Reveal`), used by File > Show Terrains Folder and Show Logs Folder |
| Frame timing | `Scripts/Unified/SC2FramePerf.cs` (`Measure`, `AddTicks`) |

## 3. Data

All of it is per user and per install, in PlayerPrefs: `SC2.Autosave.Enabled`, `SC2.Autosave.IntervalMinutes` (default 15, range 1 to 120), `SC2.Backups.MaxCount` (default 2, cap 100), `SC2.Save.WriteLegacyJson` (default on), `SC2.Customization.CheckForUpdates` (default on),
`SC2.Customization.SkippedUpdateVersion`. The log is a file, rewritten at every start (`append: false`). Nothing here is stored in a map.

## 4. Flows

- **Update check**: once per launch, only if the user has not switched it off, and not inside the Unity Editor unless a test sets `CheckInEditor`. It requests the repository's releases from the GitHub API (10 second timeout, `User-Agent` carries the current version); with no usable release it asks for the tags;
  the newest version that parses is compared with `SC2EditorVersion.Current`. A newer one that is not the skipped version opens a window (release notes, Download opens the release page, Later, Skip this version). A manual check (Customize > Check now) always answers, including "you have the latest version" and a failure message. A failure of an automatic check is only logged.
- **Autosave**: a timer in the host asks the terrain shell to save; the preferences decide whether it runs, how often, how many backups are kept and whether the large legacy JSON copy is written next to the document.
- **Logging**: `SC2Log` mirrors every message to the Unity console and to `<project>/Logs/SC2TerrainEditor.log` (project root, outside `Assets`), flushed on every line so the end of a hang or crash is not lost. File > Show Logs Folder reveals it.
- **Frame timing**: `using (SC2FramePerf.Measure("name")) { ... }` adds a section's time; once a second any section above 15 ms per second is logged as `name=ms/s(calls)`.
- **Show folders**: `Reveal` selects a file in its folder or opens a folder; false when the path does not exist or the file manager could not start.

## 5. Invariants

- **`SC2EditorVersion.Current` moves with every release** and a release is the git tag `v` plus that version; if they differ the update notice offers a version the user already has, or none.
- **Comparison is semantic versioning**: numbers first, a final version above its pre-releases, pre-release parts compared piecewise (numbers below words); text that is not a version is below any version, and drafts and non-version tags are skipped.
- **The update check never blocks the editor and never fails loudly**: a network failure is a log line, not a dialog (except for a manual check).
- **The update feed parser is pure** (a JSON text in, a release out), so it is tested without a network.
- **The log is the diagnostic channel**: a long operation logs where it is, because the file is flushed per line; do not add a log call that depends on the main thread to a worker.
- **Settings are lazy statics with a default**: a missing key must give the documented default; a new key needs one.
- **Preferences stay out of the document**, so the same map opened by someone else is not changed by them.

## 6. Editor and build differences

- The log path is derived from `Application.dataPath`'s parent, so in a built player it is next to the player's data folder, not in the repository.
- `SC2FileManager.Reveal` uses `EditorUtility.RevealInFinder` in the editor, `explorer.exe` or `open` in a Windows or macOS player, and `Application.OpenURL` elsewhere.
- The automatic update check is off inside the Unity Editor.

## 7. Extension points and how to change it

- **A new preference**: a PlayerPrefs key and property in the matching settings class with a default, a control in the right menu.
- **A new release channel**: the URLs in `SC2EditorVersion` and the parser if the answer's shape differs.
- **A new timed section**: wrap it in `SC2FramePerf.Measure`; use `AddTicks` inside a hot loop.

## 8. Editing, verification and tests

**How to edit it safely.** Do not rename a PlayerPrefs key (the user's setting is lost). Test the update logic against a local server by setting `SC2UpdateNotice.ReleasesUrl` and `TagsUrl` (they are fields for this purpose).

**Tests today**: `EditorVersionTests` (parse, compare, feed picking). The notice window, the settings, the logger and the file manager have no unit test.

**Tests a change must add.** A version or feed rule: a case in `EditorVersionTests`. A new setting: its default, plus a read after a write (an EditMode test or a scenario, because PlayerPrefs needs Unity). The notice's flow: a scenario against a local server for the three outcomes (newer, none, failure).

## 9. Known limitations and unverified behaviour

- The update check was not run against the live GitHub API for this document.
- Behaviour behind a proxy or when the API rate limit is hit was not tested; the code logs the failure.

## 10. Related documents and review

[Editor UI](EditorUi.md), [Tests and release](TestsAndRelease.md), [Document and persistence](DocumentAndPersistence.md), [Editor vs build](EditorVsBuild.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
