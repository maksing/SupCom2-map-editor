# Testing

**Writing a test is mandatory for every change of behaviour.** This page says which kind of test goes where, how to write and run it, and
what a change must test at the least. It is part of the documentation plan (`Docs/DocumentationPlan.md`, section 5); how to check a change
beyond the tests is in [Verification](Verification.md), how to edit safely is in [Editing](Editing.md).

## The policy

1. A change of behaviour comes with a test in the same commit. A new feature ships with tests of its rules and of its persistence.
2. A bug fix starts with a test that fails for the reason of the bug, and ends with the same test passing. Show that it failed: run it
   before the fix, and say so in the commit.
3. When a unit test is impossible (IMGUI layout, a shader, behaviour that exists only in a player build) a reproducible scenario takes its
   place (see [Verification](Verification.md)), together with a unit test for every rule that can be pulled out of the code. There is
   no "no test" exception, only "a test of another kind".
4. A red test is never skipped or deleted to get a green run. A test that cannot run on a machine says why, and is listed (see
   "Known baseline" below).
5. A commit that changes behaviour without its test is not finished.

## Which kind of test goes where

| What is under test | Kind | Where |
|---|---|---|
| Pure logic, file formats, anything that does not need a `UnityEngine` object | standalone xUnit, the preferred kind | `Assets/SC2/Tests~/StandaloneCharacterization` |
| Code that needs a `ScriptableObject` or another Unity type | Unity EditMode test (NUnit) | `Assets/SC2/Tests/EditMode` |
| IMGUI, shaders, saving and loading in a player, anything behind `#if !UNITY_EDITOR` | a scenario (see [Verification](Verification.md)) | planned: the harness in the repository |

### What the assemblies allow

This decides where a testable rule has to live.

- `SC2.Tests.EditMode` references only `SC2.Core`, `BdfIO.Core` and `BdfIO.Formats` (see its `.asmdef`). Code in the default assembly
  (`Assets/SC2/Scripts/Unified`, `Scripts/TerrainEditor`, `Scripts/TextureBrowser`, and the files directly in `Scripts`) cannot be called from an
  EditMode test.
- The standalone project compiles chosen source files directly (see below). It references only `UnityEngine.CoreModule.dll` from a Unity
  install, for plain value types such as `Vector3` and `Quaternion`; there is no running Unity, so it reaches only code that does not need
  a Unity object (no `ScriptableObject`, `GameObject`, `Texture`, `Mesh`).
- So a rule that should be unit tested belongs in `Scripts/Core` (or `Runtime/BdfIO`), as a plain static class with its inputs as
  parameters. The project already does this on purpose: `SC2PathGrid`, `SC2BlendMaskLayers`, `SC2CostsNavmeshRules`,
  `SC2SharedMaterials`, `SC2TexturePathSafety`. The shell calls the rule; the test calls the rule.

## Standalone xUnit tests

- Project: `Assets/SC2/Tests~/StandaloneCharacterization/SC2CharacterizationTests.csproj` (net9.0, xUnit 2.9.2, `Xunit` is a global using).
  The folder name ends in `~`, so Unity ignores it.
- Run all:

  ```
  cd Assets/SC2/Tests~/StandaloneCharacterization
  dotnet test
  ```

- Run some: `dotnet test --filter "FullyQualifiedName~PathGridTests"` (a class name, or a part of a test name).
- The project compiles the real source files by `Compile Include` lines pointing into the Unity package, so it tests what ships, with no copies.
  **A new source file is not tested until its line is added to the `.csproj`**, for example:

  ```xml
  <Compile Include="$(SC2Root)\Scripts\Core\SC2PathGrid.cs" Link="Source\SC2PathGrid.cs" />
  ```

  Test files themselves need no line: every `.cs` in the folder is compiled.
- `SC2Root` in the `.csproj` is an **absolute path** to `Assets/SC2` on the author's machine, and `UnityEngineCoreModule` is an absolute
  path to `UnityEngine.CoreModule.dll` of Unity 6000.0.77f1 under `C:\Program Files\Unity\Hub`. On another machine edit both first
  (phase 0 of the plan makes them relative or settable by an environment variable).
- If `dotnet` fails with "filename too long", the working folder is too deep: copy the project folder to a short path (for example
  `C:\Temp\SC2CharTests`) and run it there; `SC2Root` still points at the real sources.
- Conventions, from the existing tests: one class per subject named `<Subject>Tests` in `<Subject>Tests.cs`; `[Fact]` for a case,
  `[Theory]` with `[InlineData]` for a table of inputs; test names say what is expected, for example `OpenGround_GivesAFullGridJoinedToAllEightNeighboursOnce`
  or `MarkersAndLocalPaths_AreUnexportable`.
- A **characterization test** records a fact about the game's real files or about vanilla maps. Its comment names the evidence (the file, the
  map, the measurement), so the next reader can tell a fact from a guess.

## Unity EditMode tests

- Folder: `Assets/SC2/Tests/EditMode`, assembly `SC2.Tests.EditMode`, NUnit (`[Test]`, `[SetUp]`, `[TearDown]`).
- Create a Unity object with `ScriptableObject.CreateInstance<T>()` in `SetUp` and remove it with `Object.DestroyImmediate` in `TearDown`
  (see `SharedMaterialsTests` and `MaterialPairStableIdTests`).
- A new test file needs its `.meta` file with a new random guid (Unity writes it when the editor imports the file).
- Run in the editor: Window > General > Test Runner > EditMode. Run in batch (the project must not be open in another Unity):

  ```
  Unity.exe -batchmode -projectPath <project> -runTests -testPlatform EditMode -testFilter SharedMaterialsTests -testResults results.xml -logFile run.log
  ```

  This command was confirmed on a copy of the package in a scratch project; it has not yet been confirmed on this repository's own
  project.

## What every kind of change must test (the minimum)

| Change | The test must show |
|---|---|
| A new or changed saved field | save then load gives the same value; a save made before the field existed still loads, with the default; undo and redo restore it |
| An export change | the written file is read back (`TerrainBdf.Parse`, `InfoBdf.Parse` and the like) and holds what was meant; no editor marker or local path is in any exported file |
| A rule (a validator, a generator, a resolver, a planner) | its boundary cases: empty input, one item, the limit, the item just over the limit, and the case that was the bug |
| A UI change | the state after real events, in a scenario (see [Verification](Verification.md)); the rule it calls has its own unit test |
| A bug fix | the test that reproduced the bug |

## Known baseline

Recorded on commit `17863e4` with `dotnet test` on Windows:

- 1939 tests, 1938 pass, **1 known failure**: `CostsDocumentBridgeTests.BuildFromLegacy_DetectsPlayableWaterProfile`
  (expected `PlayableWater`, got `ImportedCustom`). It predates the work of that period and has not been fixed or explained here. Until it is,
  a run that shows only this failure is the baseline; any other failure is new and is yours.
- The run takes about 40 seconds.
- The count assumes the **vanilla map corpus** is present on the machine: `TestCorpus` reads a local copy of the game's `maps` folder
  (`SC2_VANILLA_MAPS_DIR`, otherwise a path on the author's machine) and `TestCorpusLua` its Lua mirror (`SC2_VANILLA_UNCOMPILED_MAPS_DIR`).
  Without it those tests are skipped with a stated reason, so the numbers differ and the format characterizations check nothing. The game
  files are never copied into the repository.
- EditMode tests: the two classes that exist were run on a copy of the package in a scratch project; a run on the repository's own project
  is not yet recorded.

Update this section when the baseline changes, with the commit and the date of the run.
