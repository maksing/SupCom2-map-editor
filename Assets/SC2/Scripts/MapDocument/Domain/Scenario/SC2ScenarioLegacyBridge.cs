using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KopiLua;
using lua_State = KopiLua.Lua.lua_State;
using CharPtr = KopiLua.Lua.CharPtr;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Read-only
    /// legacy-view snapshot, same pattern as every other domain bridge.
    /// Unlike those, the "legacy" source here isn't an existing C#
    /// object (SC2TerrainData field etc.) — it's the three raw .lua files
    /// (_scenario.lua/_save.lua/_script.lua) as they sit inside the .scd
    /// archive, which real vanilla maps ship as compiled Lua 5.1 bytecode
    /// (see Assets/SC2/Runtime/KopiLua/CHANGES_FROM_UPSTREAM.md), not text.
    ///
    /// KnownFields is populated by actually running _scenario.lua's bytes
    /// through KopiLua (SC2.ThirdParty.KopiLua) and reading the resulting
    /// ScenarioInfo table with the Lua C API — the SAME technique validated
    /// against 55/56 real *_scenario.lua files during the KopiLua port (see
    /// the status doc). _save.lua/_script.lua are stored as passthrough
    /// blobs only: _save.lua's Markers/Areas/Props already belong to the
    /// Objects domain (SC2ObjectsLegacyBridge, built earlier this session);
    /// _script.lua is mission-logic code, not data, and isn't meant to be
    /// parsed at all (see the KopiLua port status doc's _script.lua
    /// findings — it needs live-engine globals no standalone reader has).
    /// </summary>
    public static class SC2ScenarioLegacyBridge
    {
        private static readonly HashSet<string> PromotedFieldNames = new(StringComparer.Ordinal)
        {
            "name", "description", "type", "campaignID", "starts", "preview",
            "reverbPreset", "size", "map", "save", "script", "UIDeactivate", "norushradius",
            "StartPositions"
        };

        public static SC2ScenarioDocument BuildFromLegacy(
            byte[] scenarioLuaBytes,
            byte[] saveLuaBytes,
            byte[] scriptLuaBytes,
            ISC2BlobStore blobStore)
        {
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var doc = new SC2ScenarioDocument
            {
                OriginalScenarioLua = PutBlob(blobStore, scenarioLuaBytes),
                OriginalSaveLua = PutBlob(blobStore, saveLuaBytes),
                OriginalScriptLua = PutBlob(blobStore, scriptLuaBytes),
                // PatchKnownBlocks needs a text source (see SC2LuaPatchMode's
                // doc comment) — this bridge doesn't know the source format
                // was text vs bytecode by the time it's done, so it always
                // reports the safe default. A future editor UI can upgrade
                // a specific map to PatchKnownBlocks once it confirms the
                // source is text.
                ScenarioMode = SC2LuaPatchMode.OriginalPassthrough,
                SaveMode = SC2LuaPatchMode.OriginalPassthrough,
                ScriptMode = SC2LuaPatchMode.OriginalPassthrough,
            };

            if (scenarioLuaBytes == null || scenarioLuaBytes.Length == 0)
            {
                doc.State = SC2DerivedState.Missing;
                return doc;
            }

            try
            {
                var lobbyStartPositions = new List<SC2LobbyStartPosition>();
                doc.KnownFields = ReadKnownFields(scenarioLuaBytes, lobbyStartPositions);
                doc.LobbyStartPositions = lobbyStartPositions;
                doc.State = SC2DerivedState.Current;
            }
            catch (Exception ex)
            {
                // A real vanilla map (SC2_MP_001) is plain text with a
                // genuine lexer-level bug in one numeric literal — one bad
                // map must not throw out of an otherwise-working import
                // pipeline. Leave KnownFields at its empty default and mark
                // the document Invalid so a caller can surface this instead
                // of silently trusting zeroed-out fields.
                doc.State = SC2DerivedState.Invalid;
                doc.KnownFields = new SC2ScenarioKnownFields
                {
                    ExtraFields = { new SC2LuaField { Key = "__parseError", RawValue = ex.Message } }
                };
            }

            return doc;
        }

        private static SC2BlobReference PutBlob(ISC2BlobStore blobStore, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return SC2BlobReference.Empty;
            return blobStore.Put(new MemoryStream(bytes));
        }

        private static SC2ScenarioKnownFields ReadKnownFields(byte[] scenarioLuaBytes, List<SC2LobbyStartPosition> lobbyStartPositions)
        {
            var chars = new char[scenarioLuaBytes.Length];
            for (var i = 0; i < scenarioLuaBytes.Length; i++) chars[i] = (char)scenarioLuaBytes[i];
            var buf = new CharPtr(chars);

            var L = Lua.luaL_newstate();
            if (L == null) throw new InvalidOperationException("KopiLua luaL_newstate failed");
            Lua.luaL_openlibs(L);
            RegisterWrapperStubs(L);

            if (Lua.luaL_loadbuffer(L, buf, (uint)scenarioLuaBytes.Length, "scenario") != 0)
                throw new InvalidDataException("scenario.lua failed to load: " + Lua.lua_tostring(L, -1).ToString());
            if (Lua.lua_pcall(L, 0, 0, 0) != 0)
                throw new InvalidDataException("scenario.lua failed to execute: " + Lua.lua_tostring(L, -1).ToString());

            Lua.lua_getglobal(L, "ScenarioInfo");
            if (Lua.lua_type(L, -1) != Lua.LUA_TTABLE)
                throw new InvalidDataException("scenario.lua has no ScenarioInfo table");

            var fields = new SC2ScenarioKnownFields
            {
                Name = GetString(L, "name"),
                Description = GetString(L, "description"),
                Type = GetString(L, "type"),
                CampaignId = GetString(L, "campaignID"),
                Starts = GetBool(L, "starts"),
                Preview = GetString(L, "preview"),
                ReverbPreset = GetString(L, "reverbPreset"),
                MapPath = GetString(L, "map"),
                SavePath = GetString(L, "save"),
                ScriptPath = GetString(L, "script"),
                UiDeactivate = GetBool(L, "UIDeactivate"),
                NoRushRadius = GetFloat(L, "norushradius"),
            };
            (fields.SizeX, fields.SizeY) = GetIntPair(L, "size");
            ReadStartPositions(L, lobbyStartPositions);

            // ScenarioInfo is on top of the stack here — walk every key and
            // stash anything not already promoted above into ExtraFields,
            // in file (iteration) order.
            Lua.lua_pushnil(L);
            while (Lua.lua_next(L, -2) != 0)
            {
                if (Lua.lua_type(L, -2) == Lua.LUA_TSTRING)
                {
                    var key = Lua.lua_tostring(L, -2).ToString();
                    if (!PromotedFieldNames.Contains(key))
                        fields.ExtraFields.Add(new SC2LuaField { Key = key, RawValue = DescribeValue(L, -1) });
                }
                Lua.lua_pop(L, 1); // pop value, keep key for lua_next
            }

            Lua.lua_pop(L, 1); // pop ScenarioInfo
            return fields;
        }

        /// <summary>ScenarioInfo.StartPositions — <c>['ARMY_1'] = { x, y, z }</c>
        /// per army — read through the Lua API like every other field, so it
        /// works for compiled bytecode (every vanilla map) and plain text
        /// alike. ScenarioInfo must be on top of the stack; it is left there.
        /// Sorted by army number so the order doesn't depend on Lua's table
        /// iteration order.</summary>
        private static void ReadStartPositions(lua_State L, List<SC2LobbyStartPosition> into)
        {
            Lua.lua_getfield(L, -1, "StartPositions");
            if (Lua.lua_type(L, -1) == Lua.LUA_TTABLE)
            {
                Lua.lua_pushnil(L);
                while (Lua.lua_next(L, -2) != 0)
                {
                    if (Lua.lua_type(L, -2) == Lua.LUA_TSTRING && Lua.lua_type(L, -1) == Lua.LUA_TTABLE)
                    {
                        var entry = new SC2LobbyStartPosition { ArmyName = Lua.lua_tostring(L, -2).ToString() };
                        Lua.lua_rawgeti(L, -1, 1); entry.X = (float)Lua.lua_tonumber(L, -1); Lua.lua_pop(L, 1);
                        Lua.lua_rawgeti(L, -1, 2); entry.Y = (float)Lua.lua_tonumber(L, -1); Lua.lua_pop(L, 1);
                        Lua.lua_rawgeti(L, -1, 3); entry.Z = (float)Lua.lua_tonumber(L, -1); Lua.lua_pop(L, 1);
                        into.Add(entry);
                    }
                    Lua.lua_pop(L, 1); // pop value, keep key for lua_next
                }
            }
            Lua.lua_pop(L, 1); // pop StartPositions (or the nil standing in for it)
            into.Sort((a, b) => SC2LobbySpawnMath.CompareArmyNames(a.ArmyName, b.ArmyName));
        }

        private static string GetString(lua_State L, string key)
        {
            Lua.lua_getfield(L, -1, key);
            var v = Lua.lua_type(L, -1) == Lua.LUA_TSTRING ? Lua.lua_tostring(L, -1).ToString() : "";
            Lua.lua_pop(L, 1);
            return v;
        }

        private static bool GetBool(lua_State L, string key)
        {
            Lua.lua_getfield(L, -1, key);
            var v = Lua.lua_type(L, -1) == Lua.LUA_TBOOLEAN && Lua.lua_toboolean(L, -1) != 0;
            Lua.lua_pop(L, 1);
            return v;
        }

        private static float GetFloat(lua_State L, string key)
        {
            Lua.lua_getfield(L, -1, key);
            var v = Lua.lua_type(L, -1) == Lua.LUA_TNUMBER ? Lua.lua_tonumber(L, -1) : 0f;
            Lua.lua_pop(L, 1);
            return v;
        }

        private static (int X, int Y) GetIntPair(lua_State L, string key)
        {
            Lua.lua_getfield(L, -1, key);
            int x = 0, y = 0;
            if (Lua.lua_type(L, -1) == Lua.LUA_TTABLE)
            {
                Lua.lua_rawgeti(L, -1, 1);
                x = (int)Lua.lua_tonumber(L, -1);
                Lua.lua_pop(L, 1);
                Lua.lua_rawgeti(L, -1, 2);
                y = (int)Lua.lua_tonumber(L, -1);
                Lua.lua_pop(L, 1);
            }
            Lua.lua_pop(L, 1); // pop the size table itself
            return (x, y);
        }

        private static string DescribeValue(lua_State L, int idx)
        {
            switch (Lua.lua_type(L, idx))
            {
                case Lua.LUA_TSTRING: return "'" + Lua.lua_tostring(L, idx).ToString() + "'";
                case Lua.LUA_TNUMBER: return Lua.lua_tonumber(L, idx).ToString(CultureInfo.InvariantCulture);
                case Lua.LUA_TBOOLEAN: return Lua.lua_toboolean(L, idx) != 0 ? "true" : "false";
                case Lua.LUA_TTABLE: return "<table>";
                case Lua.LUA_TNIL: return "nil";
                default: return "<" + Lua.lua_typename(L, Lua.lua_type(L, idx)).ToString() + ">";
            }
        }

        // _save.lua's Markers use STRING(...)/FLOAT(...)/VECTOR3(...)-style
        // wrapper calls (real runtime function calls in the compiled
        // bytecode, not compile-time macros — see the KopiLua port status
        // doc). Real ScenarioInfo tables sampled never used them, but these
        // are registered defensively so a map that does use one for e.g.
        // `preview` still loads instead of failing on a nil global call.
        private static int ScalarStub(lua_State L) => Lua.lua_gettop(L) >= 1 ? 1 : 0;

        private static int TableStub(lua_State L)
        {
            var n = Lua.lua_gettop(L);
            Lua.lua_createtable(L, n, 0);
            for (var i = 1; i <= n; i++)
            {
                Lua.lua_pushvalue(L, i);
                Lua.lua_rawseti(L, -2, i);
            }
            return 1;
        }

        private static void RegisterWrapperStubs(lua_State L)
        {
            Lua.lua_register(L, "STRING", ScalarStub);
            Lua.lua_register(L, "FLOAT", ScalarStub);
            Lua.lua_register(L, "INTEGER", ScalarStub);
            Lua.lua_register(L, "BOOLEAN", ScalarStub);
            Lua.lua_register(L, "VECTOR3", TableStub);
            Lua.lua_register(L, "QUATERNION", TableStub);
            Lua.lua_register(L, "RECTANGLE", TableStub);
            Lua.lua_register(L, "GROUP", TableStub);
        }
    }
}
