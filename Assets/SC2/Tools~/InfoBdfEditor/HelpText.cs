namespace SC2InfoBdfEditorApp;

/// <summary>The actual "how to use this editor" text shown by HelpDialog —
/// kept in its own file just to keep HelpDialog.cs itself small.</summary>
internal static class HelpText
{
    public const string Content =
@"SC2 INFO.BDF EDITOR — HOW TO USE

================================================================
WHAT THIS IS
================================================================
This is a standalone editor for a map's info.bdf file — the binary file
that controls a Supreme Commander 2 map's environment: sun, water, fog,
glow/bloom, point clouds, fog of war / intel visibility, depth of field,
sharpen, a few render-pass toggles, air elevation, wind, and 5 texture
paths (environment/sky/color-correction/fog-of-war-correction cubes).

It is a genuinely separate Windows application — its own window, its own
taskbar icon — not a panel inside the Unity Editor. That is deliberate:
info.bdf needs to be editable even when you are testing a real standalone
game Build, where the Unity Editor is not running at all.

================================================================
GETTING STARTED
================================================================
1. File -> Open .bdf... and pick a real ""<MapName>.info.win.bdf"" file
   (it lives inside that map's own folder, e.g. under
   gamedata.scd\maps\<MapName>\).
2. The moment you open it, a full backup of the exact original bytes is
   written next to it as ""<file>.bak"" — this is your safety net, kept
   separate from Undo/Redo and from your own saves (see SAFETY NET below).
3. Edit values across the 16 tabs at the top.
4. ""Auto-save"" (checked by default) writes your edits back to that same
   file automatically about half a second after you stop editing. Turn
   it off if you would rather use File -> Save manually.

================================================================
UNDERSTANDING EACH FIELD
================================================================
Every field has a small ""?"" button next to its name. Click it to see a
description of what that field actually does, plus
(for sliders) its typical value range observed across the real vanilla
map corpus. Click the same ""?"" again, click a different field's ""?"",
switch tabs, or just move the mouse away from it, and it closes on its
own.

================================================================
TRACKING YOUR CHANGES
================================================================
- A field whose value differs from what the file had when you opened it
  is shown in a highlighted color, with a small circular-arrow reset icon
  next to it — click that icon to snap just that one field back to its
  opened value.
- Each of the 16 tab buttons shows a small dot if any field on that tab
  has changed.
- The header line shows ""N fields differ from opened file"" as a running
  total across the whole document.
- Edit -> Undo / Redo (Ctrl+Z / Ctrl+Y) steps back and forward through
  your edit history — one step per debounced burst of edits, so dragging
  a slider counts as one undo step, not one per pixel of movement.
- Edit -> Revert All to Opened State undoes every change at once, back to
  exactly what the file had when you opened it.

================================================================
TEXTURES TAB
================================================================
The 5 fields there (EnvironmentMap, SkyMap, ReservedTexturePath,
ColorCorrectionCube, FogOfWarCorrectionCube) are just STRING PATHS stored
inside info.bdf — this app has no Unity scene, so it cannot preview the
actual texture. Use ""Browse..."" to pick a real .dds file from disk; the
app then converts the absolute path you picked into the game's own
relative-path convention automatically.

FOR THIS TO WORK, the .dds file you pick must live inside a folder whose
NAME ENDS IN "".scd"" somewhere in its path (for example:
...\gamedata.scd\textures\environment\u01_env.dds). That matches the
game's own already-extracted-archive convention — point Browse at your
own manually-unpacked copy of the game's .scd archives (a folder you
created yourself by extracting a real gamedata*.scd file, named so its
own folder ends in "".scd""). If you pick a file that is NOT inside any
"".scd""-named folder, the app falls back to just the bare filename, which
is not a valid in-game path — always browse from inside a real "".scd""
folder.

================================================================
SEEING YOUR CHANGES
================================================================
This app has no live preview of its own — it only edits raw bytes. There
are two ways to actually see the effect of a change:

IN THE UNITY EDITOR: File -> Export for Unity (JSON)..., then use the
Unity-side Environment panel's ""Import JSON..."" button to pull those
exact values in and preview them live in the Editor.

IN THE ACTUAL GAME: since this app edits the real info.bdf bytes
directly (Auto-save writes straight to the file the game itself reads),
just reload the map in-game after saving to see the change. If you would
rather the game pick up further edits WITHOUT a full map reload while you
keep tweaking values here, launch the game through Steam with a launch
option that enables watching files on disk for changes. Set this under
the game's Properties -> Launch Options in Steam, for example:

    /EnableDiskWatch

or, combined with more detailed logging while you test:

    /log x.txt /showlog /windowed 1440 800 /EnableDiskWatch

Without one of these, the game only reads info.bdf once when the map
loads, and will not notice further edits until you reload the map.

================================================================
IMPORT VALUES FROM...
================================================================
File -> Import Values From... lets you selectively copy fields from a
DIFFERENT info.bdf into the one you are currently editing — useful for
reusing, say, another map's Sun/Water setup as a starting point. Pick
individual fields, or use a category's All/None, then Import.

================================================================
SAFETY NET
================================================================
The ""<file>.bak"" written the moment you open a file (see GETTING
STARTED above) is the one thing that always reflects the file's ORIGINAL
state — independent of Undo/Redo, independent of how many times you have
saved since, and it even survives closing and reopening this app.
File -> Restore Backup (.bak from when opened)... brings it back at any
time.

Auto-save always writes to the SAME file — there is no separate ""draft"".
If you want to experiment freely without touching the real file, either
use File -> Save As... to work on a copy instead, or leave Auto-save off
and only use File -> Save manually once you are happy with the result.
";
}
