using System;
using System.Collections.Generic;
using System.IO;
using KopiLua;
using lua_State = KopiLua.Lua.lua_State;
using CharPtr = KopiLua.Lua.CharPtr;

/// <summary>One entry of the game's EffectTemplates.Marker table: the name an Effect marker's EmitterTemplate
/// field refers to, and the emitter blueprints it plays.</summary>
public sealed class SC2MarkerEffectTemplate
{
    public string Name;
    public readonly List<string> Emitters = new List<string>();
}

/// <summary>
/// Reads the map-marker effects out of the game's lua/sim/EffectTemplates.lua — the
/// <c>EffectTemplates.Marker</c> table (BlinkingLight01, C02Fog01, SmokePlume01, ...) whose entries an Effect
/// marker's <c>EmitterTemplate</c> names. The file is plain data (one global table), shipped either as source
/// (the uncompiled archives) or as Lua 5.1 bytecode (the normal archives), so it is simply run in an empty Lua
/// state with no libraries and the table read back; both forms go through the same path.
/// </summary>
public static class SC2EffectTemplateNames
{
    public static List<SC2MarkerEffectTemplate> ReadMarkerTemplates(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var result = new List<SC2MarkerEffectTemplate>();

        lock (SC2BlueprintLoader.SyncRoot)
        {
            var state = Lua.luaL_newstate();
            if (state == null) throw new InvalidOperationException("KopiLua luaL_newstate failed.");
            try
            {
                var chars = new char[data.Length];
                for (var i = 0; i < data.Length; i++) chars[i] = (char)data[i];
                if (Lua.luaL_loadbuffer(state, new CharPtr(chars), (uint)data.Length, "EffectTemplates") != 0)
                    throw new InvalidDataException("EffectTemplates did not load: " + Lua.lua_tostring(state, -1).ToString());
                if (Lua.lua_pcall(state, 0, 0, 0) != 0)
                    throw new InvalidDataException("EffectTemplates did not run: " + Lua.lua_tostring(state, -1).ToString());

                Lua.lua_getfield(state, Lua.LUA_GLOBALSINDEX, "EffectTemplates");
                if (Lua.lua_type(state, -1) != Lua.LUA_TTABLE) return result;
                Lua.lua_getfield(state, -1, "Marker");
                if (Lua.lua_type(state, -1) != Lua.LUA_TTABLE) return result;

                var marker = Lua.lua_gettop(state);
                Lua.lua_pushnil(state);
                while (Lua.lua_next(state, marker) != 0)
                {
                    if (Lua.lua_type(state, -2) == Lua.LUA_TSTRING && Lua.lua_type(state, -1) == Lua.LUA_TTABLE)
                    {
                        var template = new SC2MarkerEffectTemplate { Name = Lua.lua_tostring(state, -2).ToString() };
                        var table = Lua.lua_gettop(state);
                        var count = (int)Lua.lua_objlen(state, table);
                        for (var i = 1; i <= count; i++)
                        {
                            Lua.lua_rawgeti(state, table, i);
                            if (Lua.lua_type(state, -1) == Lua.LUA_TSTRING) template.Emitters.Add(Lua.lua_tostring(state, -1).ToString());
                            Lua.lua_pop(state, 1);
                        }
                        result.Add(template);
                    }
                    Lua.lua_pop(state, 1); // keep the key for lua_next
                }
            }
            finally
            {
                Lua.lua_close(state);
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
