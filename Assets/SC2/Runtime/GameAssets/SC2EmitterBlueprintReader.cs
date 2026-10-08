using System;
using System.IO;
using KopiLua;
using lua_State = KopiLua.Lua.lua_State;
using CharPtr = KopiLua.Lua.CharPtr;

/// <summary>
/// Reads a game particle emitter blueprint (effects/Emitters/**/*.bp). The file is one call, <c>EmitterBlueprint { ... }</c>, shipped as
/// source (the uncompiled archives) or as Lua 5.1 bytecode (the normal archives); both are run in an empty Lua state in which
/// <c>EmitterBlueprint</c> only keeps its argument, and the table is read back.
/// </summary>
public static class SC2EmitterBlueprintReader
{
    public static SC2EmitterBlueprint Read(byte[] data, string name = "")
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        lock (SC2BlueprintLoader.SyncRoot)
        {
            var state = Lua.luaL_newstate();
            if (state == null) throw new InvalidOperationException("KopiLua luaL_newstate failed.");
            try
            {
                Run(state, "EmitterBlueprint = function(t) CAPTURED = t end", "capture");
                Run(state, data, "emitter " + name);

                Lua.lua_getfield(state, Lua.LUA_GLOBALSINDEX, "CAPTURED");
                if (Lua.lua_type(state, -1) != Lua.LUA_TTABLE)
                    throw new InvalidDataException("The file does not define an EmitterBlueprint.");
                var table = Lua.lua_gettop(state);

                var blueprint = new SC2EmitterBlueprint
                {
                    Name = name,
                    Texture = String(state, table, "Texture"),
                    RampTexture = String(state, table, "RampTexture"),
                    BlendMode = (int)Math.Round(Number(state, table, "Blendmode", 0)),
                    Lifetime = Number(state, table, "Lifetime", -1),
                    RepeatTime = Number(state, table, "Repeattime", 0),
                    Flat = Bool(state, table, "Flat", false),
                    AlignRotation = Bool(state, table, "AlignRotation", false),
                    LocalVelocity = Bool(state, table, "LocalVelocity", true),
                    LocalAcceleration = Bool(state, table, "LocalAcceleration", false),
                    ParticleResistance = Bool(state, table, "ParticleResistance", false),
                    SnapToWaterline = Bool(state, table, "SnapToWaterline", false),
                    LodCutoff = Number(state, table, "LODCutoff", 0),
                    TextureStripCount = Math.Max(1, (int)Math.Round(Number(state, table, "TextureStripcount", 1))),
                };

                foreach (var curveName in SC2EmitterBlueprint.CurveNames)
                {
                    var curve = ReadCurve(state, table, curveName);
                    if (curve != null) blueprint.Curves[curveName] = curve;
                }
                return blueprint;
            }
            finally
            {
                Lua.lua_close(state);
            }
        }
    }

    private static void Run(lua_State state, string source, string chunkName)
    {
        var bytes = new byte[source.Length];
        for (var i = 0; i < source.Length; i++) bytes[i] = (byte)source[i];
        Run(state, bytes, chunkName);
    }

    private static void Run(lua_State state, byte[] data, string chunkName)
    {
        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++) chars[i] = (char)data[i];
        if (Lua.luaL_loadbuffer(state, new CharPtr(chars), (uint)data.Length, chunkName) != 0)
            throw new InvalidDataException(chunkName + " did not load: " + Lua.lua_tostring(state, -1).ToString());
        if (Lua.lua_pcall(state, 0, 0, 0) != 0)
            throw new InvalidDataException(chunkName + " did not run: " + Lua.lua_tostring(state, -1).ToString());
    }

    private static string String(lua_State state, int table, string field)
    {
        Lua.lua_getfield(state, table, field);
        var value = Lua.lua_type(state, -1) == Lua.LUA_TSTRING ? Lua.lua_tostring(state, -1).ToString() : "";
        Lua.lua_pop(state, 1);
        return value;
    }

    private static float Number(lua_State state, int table, string field, float fallback)
    {
        Lua.lua_getfield(state, table, field);
        var value = Lua.lua_type(state, -1) == Lua.LUA_TNUMBER ? (float)Lua.lua_tonumber(state, -1) : fallback;
        Lua.lua_pop(state, 1);
        return value;
    }

    private static bool Bool(lua_State state, int table, string field, bool fallback)
    {
        Lua.lua_getfield(state, table, field);
        var value = Lua.lua_type(state, -1) == Lua.LUA_TBOOLEAN ? Lua.lua_toboolean(state, -1) != 0 : fallback;
        Lua.lua_pop(state, 1);
        return value;
    }

    private static SC2EmitterCurve ReadCurve(lua_State state, int table, string field)
    {
        Lua.lua_getfield(state, table, field);
        try
        {
            if (Lua.lua_type(state, -1) != Lua.LUA_TTABLE) return null;
            var curveTable = Lua.lua_gettop(state);
            var curve = new SC2EmitterCurve { XRange = Number(state, curveTable, "XRange", 0) };

            Lua.lua_getfield(state, curveTable, "Keys");
            if (Lua.lua_type(state, -1) == Lua.LUA_TTABLE)
            {
                var keys = Lua.lua_gettop(state);
                var count = (int)Lua.lua_objlen(state, keys);
                for (var i = 1; i <= count; i++)
                {
                    Lua.lua_rawgeti(state, keys, i);
                    if (Lua.lua_type(state, -1) == Lua.LUA_TTABLE)
                    {
                        var key = Lua.lua_gettop(state);
                        curve.Keys.Add(new SC2EmitterCurveKey(Number(state, key, "x", 0), Number(state, key, "y", 0), Number(state, key, "z", 0)));
                    }
                    Lua.lua_pop(state, 1);
                }
            }
            Lua.lua_pop(state, 1);
            curve.Keys.Sort((a, b) => a.X.CompareTo(b.X));
            return curve;
        }
        finally
        {
            Lua.lua_pop(state, 1);
        }
    }
}
