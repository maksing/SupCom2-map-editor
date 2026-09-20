using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>How much of a
    /// source .lua file's original content this document lets an editor
    /// safely regenerate on export.
    ///
    /// Only OriginalPassthrough is ever produced by SC2ScenarioLegacyBridge
    /// today: PatchKnownBlocks needs a text source to locate/replace
    /// specific {...} blocks (SC2LuaBlocks' brace-counting — see
    /// SC2ScenarioLuaWriter/SC2SaveLuaWriter, which already do this for
    /// text-sourced files), which real vanilla maps don't have (55/56
    /// sampled *_scenario.lua / *_save.lua files are compiled Lua 5.1
    /// bytecode, confirmed by header-byte inspection) — there is no
    /// decompiler in this project (KopiLua is a compiler+VM port, not a
    /// decompiler) to turn bytecode back into patchable text.
    /// GenerateSimpleTemplate has no producer anywhere yet.</summary>
    public enum SC2LuaPatchMode
    {
        OriginalPassthrough,
        PatchKnownBlocks,
        GenerateSimpleTemplate
    }

    /// <summary>Scoped to the ScenarioInfo fields that are
    /// (a) scalar, (b) present the same way across every real vanilla map
    /// sampled (56 uncompiled *_scenario.lua files), and (c) plausible
    /// editor-UI fields (title/description/type/paths) — not a full mirror
    /// of every ScenarioInfo key. Two field families are deliberately left
    /// out of this typed shape and land in ExtraFields instead:
    ///  - the per-army norushoffsetX_/norushoffsetY_ARMY_* pairs (key name
    ///    depends on how many armies the map has — not a fixed field set);
    ///  - Configurations (nested tables, not scalars). StartPositions is
    ///    NOT in ExtraFields either: it is the lobby-minimap position of each
    ///    army, kept apart from the in-game spawn the Objects domain owns
    ///    (SC2ObjArmy/markers), in SC2ScenarioDocument.LobbyStartPositions.</summary>
    public sealed class SC2ScenarioKnownFields
    {
        public string Name = "";
        public string Description = "";
        public string Type = "";
        public string CampaignId = "";
        public bool Starts;
        public string Preview = "";
        public string ReverbPreset = "";
        public int SizeX;
        public int SizeY;
        public string MapPath = "";
        public string SavePath = "";
        public string ScriptPath = "";
        public bool UiDeactivate;
        public float NoRushRadius;

        /// <summary>Every other top-level ScenarioInfo key this bridge saw
        /// but didn't promote to a typed field above, as raw key/rawValue
        /// pairs in file order — same SC2LuaField shape SC2SaveLuaParser
        /// already uses for _save.lua marker fields, so a future viewer can
        /// treat both the same way.</summary>
        public List<SC2LuaField> ExtraFields = new();
    }

    /// <summary>
    /// Built today only by
    /// SC2ScenarioLegacyBridge as a read-only snapshot — nothing
    /// writes live edits or regenerates any of the three source files
    /// through this yet.
    ///
    /// Original*Lua are SC2BlobReference, not the plan's literal `string`:
    /// real files are compiled Lua 5.1 bytecode (binary, not text — see the
    /// KopiLua port's validation notes), so a C# string can't hold them
    /// byte-faithfully. KnownFields is populated by actually loading and
    /// executing OriginalScenarioLua's bytes through KopiLua and reading the
    /// resulting ScenarioInfo table (works uniformly for bytecode or text
    /// sources, since Lua's own loader auto-detects the format) rather than
    /// by text-parsing — unlike SC2ScenarioLuaWriter/SC2SaveLuaWriter's
    /// existing regex+brace-counting approach, which requires text and
    /// would silently find nothing in a bytecode file.
    /// </summary>
    public sealed class SC2ScenarioDocument
    {
        public SC2BlobReference OriginalScenarioLua;
        public SC2BlobReference OriginalSaveLua;
        public SC2BlobReference OriginalScriptLua;

        public SC2ScenarioKnownFields KnownFields = new();

        /// <summary>Where each army's marker is drawn on the lobby minimap
        /// (_scenario.lua StartPositions) — edited in the Lobby Spawns window,
        /// never touches the in-game spawns in _save.lua. An army with no
        /// entry here exports its marker position (see
        /// SC2ObjectsExport.RegenerateScenarioLua).</summary>
        public List<SC2LobbyStartPosition> LobbyStartPositions = new();

        /// <summary>The map-select preview (Scenario > Preview): opaque BGRA pixels, 448x206. Empty = none captured
        /// or imported (export then writes a black picture with the frame).</summary>
        public SC2BlobReference MapShotBgra;

        /// <summary>An imported custom minimap picture as PNG bytes. Empty = the minimap is rendered from the map.</summary>
        public SC2BlobReference CustomMinimapPng;

        public SC2LuaPatchMode ScenarioMode;
        public SC2LuaPatchMode SaveMode;
        public SC2LuaPatchMode ScriptMode;

        public SC2DerivedState State;
    }
}
