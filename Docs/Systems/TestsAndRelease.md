# Tests, verification harness and release process

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Testing](../Process/Testing.md) and [Verification](../Process/Verification.md) first: they are the rules; this document is the map of what exists.

## 1. Purpose and boundaries

What the repository has to check a change (two test projects and the scratch scenarios), how those fit together, and how a version becomes a release. The policy (a test is mandatory for every change of behaviour) and the how-to are in the process documents; this page describes the machinery and its gaps.

Not included: the tests of one system (each system document lists its own), the editing rules ([Editing](../Process/Editing.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Standalone suite | `Assets/SC2/Tests~/StandaloneCharacterization/` (`SC2CharacterizationTests.csproj`, 107 `.cs` files including the `TestCorpus*.cs` fixtures, an empty `Shims/` folder); run with `dotnet test` |
| EditMode suite | `Assets/SC2/Tests/EditMode/` (`SC2.Tests.EditMode.asmdef`; `MaterialPairStableIdTests`, `ObjectsLuaCharacterizationTests`, `SharedMaterialsTests`); the asmdef references only `SC2.Core` and the BdfIO assemblies |
| Test helpers | `ImportServiceTestFixtures.cs` (builds a small synthetic multi-map `.scd`, as a zip or a directory tree, with a valid platform magic and padding as the `.bdf` payload, so the import service tests need no vanilla corpus), `JsonTests.cs` (the small JSON value reader and writer `SC2JsonValue` of the document layer) |
| Scenario harness | **not in the repository**: scratch Unity projects outside it run headless scenarios (export, bake, paint, render, a player run that reads its own report). See [Verification](../Process/Verification.md) |
| Version | `Scripts/Unified/SC2EditorVersion.cs` (`Current`, `Repository`, the release and tag API URLs) |
| Update feed | `SC2UpdateFeed` (pure), `SC2UpdateNotice` ([support](Support.md)) |
| PR rules | `CONTRIBUTING.md`, `.github/pull_request_template.md` |
| Build | Unity "Build & Run" to a Windows standalone player; `README.md` "Building from source" (Unity 6000.0.77f1) |

## 3. Data

**The standalone project compiles real source files** with explicit `Compile Include` lines under `$(SC2Root)` (a few use globs, e.g. `Runtime/BdfIO/Core/**/*.cs`), so a new file under `Core`, `MapDocument` or `BdfIO` is tested only after its line is added. `SC2Root` is an absolute path to `Assets/SC2` on the
author's machine. Many tests are **characterizations**: they pin a fact measured on the real vanilla maps, and their comment names the evidence. **The vanilla data is not in the repository.** `TestCorpus` reads the maps folder of a local copy of the game data (`SC2_VANILLA_MAPS_DIR`, else a path on the author's machine), `TestCorpusLua` reads the plain-text Lua mirror
(`SC2_VANILLA_UNCOMPILED_MAPS_DIR`), and `TestCorpusLuaCompiled` the compiled Lua. When the folder is missing the class reports a skip reason and these tests are skipped, not failed, so **a run on another machine passes with far fewer real checks than the recorded baseline**. The recorded baseline is in [Testing](../Process/Testing.md#known-baseline).

## 4. Flows

- **Before a commit**: run the standalone suite (`dotnet test`); if the change touches a Unity-dependent type, the EditMode suite in the Unity Test Runner; then the scenario of the touched system and a player compile, as [Verification](../Process/Verification.md) orders them.
- **A bug fix**: write the failing test first, then the fix.
- **A release** (what the code supports): set `SC2EditorVersion.Current` to the new version, commit, tag the commit `v<version>` (for example `v1.0.0-beta`; tags so far run from `v0.0.6-alpha` to `v1.0.0-beta`), build the Windows player, publish a GitHub release on `maksing/SupCom2-map-editor` with the build and its notes. Running editors then see it through the update notice, which prefers
  releases and falls back to tags.

## 5. Invariants

- **A test names the behaviour, not the method** (`Method_Condition_Result`) and, for a vanilla fact, the evidence in a comment.
- **A skipped or deleted failing test is not a fix**; the one known failure is listed with its name until it is understood.
- **`SC2EditorVersion.Current` equals the newest tag at release time**; the update notice compares exactly these.
- **The standalone project reaches only code free of Unity objects.** It references `UnityEngine.CoreModule.dll` for plain value types (`Vector3`, `Quaternion`); code that needs a `ScriptableObject`, `GameObject`, `Texture` or `Mesh` belongs in EditMode tests or the harness. The repository's habit of putting pure rules in `Core` exists for this reason.
- **A player compile is a separate check**: `#if !UNITY_EDITOR` branches (the runtime shim, runtime persistence, popups) are not compiled by the editor or by the test projects.
- **User data is read-only for tests and scenarios**: a player run uses a throwaway save folder, never `LocalLow\BDFTM\...`.

## 6. Editor and build differences

- The test projects run on the .NET SDK, not in Unity. EditMode tests run in the editor only. The only check that compiles the player-only code is a real player build.

## 7. Extension points and how to change it

- **A new standalone test**: a file in `Tests~/StandaloneCharacterization`, and a `Compile Include` for every source file it needs that is not already included.
- **A new EditMode test**: a file in `Tests/EditMode` with its `.meta` (a new guid); the asmdef must reference the assembly under test.
- **A new fixture**: a helper in `TestCorpus*.cs` that reads from the same local game-data folder and reports the skip reason when it is absent; never copy game files into the repository.
- **A new release step**: here and in `CONTRIBUTING.md`.

## 8. Editing, verification and tests

**How to edit it safely.** Keep `dotnet test` green, or equal to the recorded baseline, before and after. Do not move `SC2Root` without updating [Testing](../Process/Testing.md). When a source file moves, fix its `Compile Include`; the failure is a compile error, not a skipped test.

**Tests today**: about 1940 tests in the standalone suite, 1 known failure (`CostsDocumentBridgeTests.BuildFromLegacy_DetectsPlayableWaterProfile`, unexplained); three EditMode test classes. These counts are from the last recorded run on commit `17863e4`; rerun before relying on them.

**Tests a change must add.** A change to the version or feed code: `EditorVersionTests`. A change to the test setup: a run of the whole suite before and after, with both counts in the commit message.

## 9. Known limitations and unverified behaviour

- **There is no one-command check script and no in-repository scenario harness yet.** Both are named as planned in [DocumentationPlan](../DocumentationPlan.md); until they exist, the Unity-dependent behaviour is checked by scratch scenarios that other people cannot reproduce.
- **No CI**: the repository has none, so nothing runs the suite for a pull request.
- `SC2Root` and the path of `UnityEngine.CoreModule.dll` (Unity 6000.0.77f1 under `C:\Program Files\Unity\Hub`) in the project file are absolute, so the suite does not run on another machine without editing both.
- The `dotnet test` "filename too long" workaround and the one known failure are described in [Testing](../Process/Testing.md).
- The release steps above are derived from the code and the tags; the build and upload were not performed for this document, and no release script exists in the repository.

## 10. Related documents and review

[Process: Testing](../Process/Testing.md), [Process: Verification](../Process/Verification.md), [Process: Editing](../Process/Editing.md), [Support](Support.md), [Editor vs build](EditorVsBuild.md), `CONTRIBUTING.md`.
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
