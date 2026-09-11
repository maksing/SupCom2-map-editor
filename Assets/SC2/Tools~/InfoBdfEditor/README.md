# SC2 Info.bdf Editor (standalone)

A genuinely independent Windows app for editing `info.bdf` — its own
taskbar icon, minimizable/movable independently of Unity. Reads/writes real
`.bdf` files directly (same `BdfIO.Core`/`BdfIO.Formats.Info` code Unity
itself uses, compiled straight from this repo — see the `.csproj`). It does
not talk to a running Unity process; values move over via a one-way JSON
export/import (`InfoBdfJsonDto.cs`, shared by both sides).

Rendered with **Dear ImGui** (via Hexa.NET.ImGui + a real Direct3D 11
device), matching the same immediate-mode paradigm and dark color palette
Unity's own `SC2EnvFieldGui.cs`/unified-shell panels use — not WinForms'
native controls. `System.Windows.Forms.Form` is still used for exactly one
thing: obtaining a real Win32 window/HWND and running its message pump; no
WinForms `Control` is ever placed on it. See `MainForm.cs`'s and the
`.csproj`'s header comments for why Win32+D3D11 was chosen over an initial
GLFW+OpenGL attempt (a native crash traced to Dear ImGui's per-backend-DLL
context pointer not propagating automatically — fixed by explicitly calling
`SetCurrentContext` on every backend module).

## Build

```
dotnet build
```

If `dotnet build`/`publish` fails with a "filename too long" error, copy
this whole `Tools~/InfoBdfEditor` folder to a short path (e.g.
`C:\Temp\InfoBdfEditor`) and build from there — same caveat
`Tests~/StandaloneCharacterization/SC2CharacterizationTests.csproj` already
documents for this repo's deep folder structure.

## Publish (what you actually ship)

```
dotnet publish -c Release -r win-x64 --self-contained
```

**Do NOT add `-p:PublishSingleFile=true`** — confirmed via two real crash
reports from actual published+deployed runs that this breaks native
interop here (`DllNotFoundException` loading `cimgui.dll`, even with
`IncludeNativeLibrariesForSelfExtract=true`; HexaGen's native loader
doesn't know to look in PublishSingleFile's temp self-extraction
directory). The command above produces a self-contained **folder** under
`bin/Release/net9.0-windows/win-x64/publish/` — `SC2InfoBdfEditorApp.exe`
plus every native `Hexa.NET.*`/`cimgui.dll`/`ImGuiImpl.dll` dependency
sitting right next to it, where the native loader actually finds them —
no .NET runtime install required on the target machine.

## Wiring it up next to your Build

Copy the **entire published folder's contents** (not just the exe) into an
`InfoBdfEditor` subfolder next to your Standalone Player's own `.exe` (i.e.
a sibling of `<ProductName>_Data`):

```
YourBuildFolder/
  YourGame.exe
  YourGame_Data/
  InfoBdfEditor/
    SC2InfoBdfEditorApp.exe
    cimgui.dll
    Hexa.NET.*.dll
    ImGuiImpl.dll
    ... (every other file from the publish folder)
```

The in-game panel's "Open Info.bdf Editor (external)" button resolves this
exact path and launches it — it reports a clear status message (never
crashes) if the exe isn't there yet.

## Testing

`Tests~/InfoBdfEditorTests/InfoBdfEditorTests.csproj` — real xUnit tests,
same source-sharing pattern as `Tests~/StandaloneCharacterization`:

```
dotnet test Tests~/InfoBdfEditorTests
```

Covers data-layer round-trip correctness (InfoBdf Parse/Serialize,
InfoBdfJsonDto, InfoFieldLimits sanity) and — the part that actually
exercises the real render pipeline — constructs a real off-screen
`MainForm`/D3D11/ImGui window and drives every one of its 16 tabs against
every real vanilla `info.bdf` in the corpus, asserting no exception. This
is the test that would have caught the two real UI bugs found during
development (invisible tab strip; menu bar rendering behind it) — both
were pure rendering/layout bugs a data-only test can never see.

Needs the same real vanilla map corpus `Tests~/StandaloneCharacterization`
uses (`TestCorpus.cs`, shared as-is) — skips gracefully (0 test cases, not
a failure) if that corpus isn't present on the machine running them.

## Workflow

1. In-game: Tools → "Info.bdf Editor..." → "Open Info.bdf Editor (external)".
2. In the external app: File → Open .bdf... (or start from scratch), edit,
   File → Export for Unity (JSON)....
3. Back in-game: "Import JSON..." in the same panel, pick the exported
   `.json` — the live preview and "Save to Map"/"Save to File" buttons then
   work exactly as if you'd opened the `.bdf` directly in the panel.
