// SC2SaveLuaBytecodeReader.cs
//
// Reads a real _save.lua/_scenario.lua file when it's Lua 5.1 BYTECODE
// instead of plain text - the real shipped/compiled form these files take
// once extracted from an actual .scd archive (as opposed to the
// "uncompiled" reference source corpus this project's other Lua tooling was
// verified against). SC2ObjectsImport's existing parsers (SC2SaveLuaParser,
// SC2LuaBlocks, the regex-based Areas/Chains/Armies readers) are text-only -
// fed bytecode bytes via File.ReadAllText, they silently find nothing
// (every SC2LuaBlocks.FindBlock call returns -1, since the "text" is really
// a mangled UTF-8 decode of binary bytes), which is why Chains/Armies/Units
// (present ONLY in _save.lua, never in mapobjs.bdf) disappeared entirely on
// a bytecode-sourced import while mapobjs.bdf-derived Props/Markers still
// partly worked.
//
// Reuses the exact bytecode-execution approach already proven in
// SC2BlueprintLoader (register the file's own helper functions, luaL_
// loadbuffer + lua_pcall, then read the resulting Lua table) - NOT shared
// code with that file, to avoid any risk of regressing real blueprint
// loading; this is a small, self-contained duplicate of the same KopiLua
// plumbing, scoped to save.lua/scenario.lua's own needs.
//
// Real _save.lua/_scenario.lua wrap every leaf value in a helper function
// call - STRING(...)/FLOAT(...)/BOOLEAN(...)/VECTOR3(...)/QUATERNION(...)/
// RECTANGLE(...)/GROUP{...} (confirmed exhaustively against the real
// vanilla corpus, gamedata.scd/uncompiled/maps - no other such call is ever
// used). Each is registered here to return a TAGGED table (a
// "__sc2_wrap__" string field naming the wrapper, plus its argument(s) in
// numbered "__sc2_v1__".."__sc2_v4__" fields) rather than collapsing to a
// bare value - GROUP specifically must stay distinguishable from a plain
// nested table, since SC2ObjectsImport's own Units-tree reader treats a
// GROUP-wrapped entry as a sub-group vs. a leaf unit.

using System;
using System.Collections.Generic;
using System.IO;
using KopiLua;
using UnityEngine;
using lua_State = KopiLua.Lua.lua_State;
using CharPtr = KopiLua.Lua.CharPtr;

public static class SC2SaveLuaBytecodeReader
{
    private const string WrapTag = "__sc2_wrap__";

    /// <summary>Same Lua 5.1 bytecode signature check as
    /// SC2BlueprintLoader.IsLua51Bytecode - not called directly since
    /// SC2.Core (this file's assembly) doesn't reference SC2.GameAssets
    /// (that one's), and the check itself is 5 lines, not worth a new
    /// cross-assembly dependency for.</summary>
    public static bool IsBytecode(byte[] bytes) =>
        bytes != null && bytes.Length >= 5 && bytes[0] == 0x1b && bytes[1] == (byte)'L' && bytes[2] == (byte)'u' && bytes[3] == (byte)'a' && bytes[4] == 0x51;

    /// <summary>Executes the bytecode and returns the named global variable
    /// (e.g. "Scenario") as a tree, or null if the global doesn't exist,
    /// isn't a table, or the bytecode fails to load/run.</summary>
    public static SC2LuaBytecodeValue ReadGlobalTable(byte[] bytes, string globalName)
    {
        var state = Lua.luaL_newstate();
        if (state == null) throw new InvalidOperationException("KopiLua luaL_newstate failed.");
        try
        {
            Lua.lua_register(state, "STRING", L => TagWrapper(L, "STRING", 1));
            Lua.lua_register(state, "FLOAT", L => TagWrapper(L, "FLOAT", 1));
            Lua.lua_register(state, "BOOLEAN", L => TagWrapper(L, "BOOLEAN", 1));
            Lua.lua_register(state, "VECTOR3", L => TagWrapper(L, "VECTOR3", 3));
            Lua.lua_register(state, "QUATERNION", L => TagWrapper(L, "QUATERNION", 4));
            Lua.lua_register(state, "RECTANGLE", L => TagWrapper(L, "RECTANGLE", 4));
            Lua.lua_register(state, "GROUP", TagGroup);

            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            var buffer = new CharPtr(chars);
            if (Lua.luaL_loadbuffer(state, buffer, (uint)bytes.Length, globalName) != 0)
                throw new InvalidDataException("Bytecode failed to load: " + Lua.lua_tostring(state, -1).ToString());
            if (Lua.lua_pcall(state, 0, 0, 0) != 0)
                throw new InvalidDataException("Bytecode failed to execute: " + Lua.lua_tostring(state, -1).ToString());

            Lua.lua_getglobal(state, globalName);
            if (Lua.lua_type(state, -1) != Lua.LUA_TTABLE) { Lua.lua_pop(state, 1); return null; }
            var result = ReadValue(state, Lua.lua_gettop(state), new HashSet<object>());
            Lua.lua_pop(state, 1);
            return result;
        }
        finally { Lua.lua_close(state); }
    }

    // ---- Constructor-wrapper registration ----

    private static int TagWrapper(lua_State state, string wrapName, int argCount)
    {
        Lua.lua_createtable(state, 0, argCount + 1);
        var tableIndex = Lua.lua_gettop(state);
        Lua.lua_pushstring(state, wrapName);
        Lua.lua_setfield(state, tableIndex, WrapTag);
        for (var i = 1; i <= argCount; i++)
        {
            Lua.lua_pushvalue(state, i);
            Lua.lua_setfield(state, tableIndex, "__sc2_v" + i + "__");
        }
        return 1;
    }

    /// <summary>GROUP is called as GROUP { ... } (Lua's f{...} call sugar) -
    /// argument 1 is already the table literal; tag it in place instead of
    /// wrapping it a second time, so its own real fields (e.g. "Units")
    /// stay directly readable.</summary>
    private static int TagGroup(lua_State state)
    {
        Lua.lua_pushstring(state, "GROUP");
        Lua.lua_setfield(state, 1, WrapTag);
        Lua.lua_pushvalue(state, 1);
        return 1;
    }

    // ---- Generic table walker (mirrors SC2BlueprintLoader.ReadValue/ReadTable) ----

    private static SC2LuaBytecodeValue ReadValue(lua_State state, int index, HashSet<object> activeTables)
    {
        switch (Lua.lua_type(state, index))
        {
            case Lua.LUA_TNIL: return SC2LuaBytecodeValue.Nil();
            case Lua.LUA_TBOOLEAN: return SC2LuaBytecodeValue.Bool(Lua.lua_toboolean(state, index) != 0);
            case Lua.LUA_TNUMBER: return SC2LuaBytecodeValue.Num(Lua.lua_tonumber(state, index));
            case Lua.LUA_TSTRING: return SC2LuaBytecodeValue.Str(Lua.lua_tostring(state, index).ToString());
            case Lua.LUA_TTABLE: return ReadTable(state, index, activeTables);
            default: return SC2LuaBytecodeValue.Nil(); // functions/userdata never appear as real data here
        }
    }

    private static SC2LuaBytecodeValue ReadTable(lua_State state, int index, HashSet<object> activeTables)
    {
        object identity = Lua.lua_topointer(state, index);
        if (identity == null || !activeTables.Add(identity))
            return SC2LuaBytecodeValue.Nil(); // cyclic reference - never legitimately happens in this file format

        try
        {
            var table = new SC2LuaBytecodeTable();
            var itemCount = (int)Lua.lua_objlen(state, index);
            for (var item = 1; item <= itemCount; item++)
            {
                Lua.lua_rawgeti(state, index, item);
                table.Items.Add(ReadValue(state, Lua.lua_gettop(state), activeTables));
                Lua.lua_pop(state, 1);
            }

            Lua.lua_pushnil(state);
            while (Lua.lua_next(state, index) != 0)
            {
                var keyType = Lua.lua_type(state, -2);
                var isSequentialItem = keyType == Lua.LUA_TNUMBER && IsSequentialItem(Lua.lua_tonumber(state, -2), itemCount);
                if (!isSequentialItem)
                {
                    var key = keyType == Lua.LUA_TSTRING
                        ? Lua.lua_tostring(state, -2).ToString()
                        : Lua.lua_tonumber(state, -2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    table.Fields[key] = ReadValue(state, Lua.lua_gettop(state), activeTables);
                }
                Lua.lua_pop(state, 1); // keep the key for lua_next
            }
            return SC2LuaBytecodeValue.Tab(table);
        }
        finally { activeTables.Remove(identity); }
    }

    private static bool IsSequentialItem(double value, int itemCount)
    {
        var integer = (int)value;
        return value == integer && integer >= 1 && integer <= itemCount;
    }

    // ---- Accessors for tagged wrapper values ----

    public static string WrapperOf(SC2LuaBytecodeValue value)
    {
        if (value?.Kind != SC2LuaBytecodeValueKind.Table) return null;
        return value.TableValue.Fields.TryGetValue(WrapTag, out var tag) && tag.Kind == SC2LuaBytecodeValueKind.String
            ? tag.StringValue : null;
    }

    private static SC2LuaBytecodeValue WrapArg(SC2LuaBytecodeValue value, int n) =>
        value?.Kind == SC2LuaBytecodeValueKind.Table && value.TableValue.Fields.TryGetValue("__sc2_v" + n + "__", out var v) ? v : null;

    /// <summary>Unwraps STRING(...), or accepts a bare Lua string with no
    /// wrapper at all (real Armies blocks store personality/plans/type/
    /// Alliances values as plain 'text' with no STRING() around them - same
    /// tolerance SC2LuaValue.ExtractString already has for the text path).</summary>
    public static string AsString(SC2LuaBytecodeValue value)
    {
        if (value == null) return null;
        if (value.Kind == SC2LuaBytecodeValueKind.String) return value.StringValue;
        if (WrapperOf(value) == "STRING") return WrapArg(value, 1)?.StringValue;
        return null;
    }

    /// <summary>Unwraps FLOAT(...), or accepts a bare number (real files use
    /// plain numeric literals in a few spots, e.g. Economy.mass/energy,
    /// Units' bare "faction = 0").</summary>
    public static float? AsFloat(SC2LuaBytecodeValue value)
    {
        if (value == null) return null;
        if (value.Kind == SC2LuaBytecodeValueKind.Number) return (float)value.NumberValue;
        if (WrapperOf(value) == "FLOAT")
        {
            var v = WrapArg(value, 1);
            return v?.Kind == SC2LuaBytecodeValueKind.Number ? (float)v.NumberValue : null;
        }
        return null;
    }

    public static bool? AsBool(SC2LuaBytecodeValue value)
    {
        if (value == null) return null;
        if (value.Kind == SC2LuaBytecodeValueKind.Boolean) return value.BoolValue;
        if (WrapperOf(value) == "BOOLEAN")
        {
            var v = WrapArg(value, 1);
            return v?.Kind == SC2LuaBytecodeValueKind.Boolean ? v.BoolValue : null;
        }
        return null;
    }

    /// <summary>VECTOR3(x, y, z) - matches SC2LuaValue.ExtractVector3's own component order.</summary>
    public static Vector3? AsVector3(SC2LuaBytecodeValue value)
    {
        if (WrapperOf(value) != "VECTOR3") return null;
        var x = WrapArg(value, 1); var y = WrapArg(value, 2); var z = WrapArg(value, 3);
        if (x?.Kind != SC2LuaBytecodeValueKind.Number || y?.Kind != SC2LuaBytecodeValueKind.Number || z?.Kind != SC2LuaBytecodeValueKind.Number)
            return null;
        return new Vector3((float)x.NumberValue, (float)y.NumberValue, (float)z.NumberValue);
    }

    /// <summary>QUATERNION(x, y, z, w) - matches SC2LuaValue.ExtractQuaternion's own component order (the identity is 0, 0, 0, 1).</summary>
    public static Quaternion? AsQuaternion(SC2LuaBytecodeValue value)
    {
        if (WrapperOf(value) != "QUATERNION") return null;
        var x = WrapArg(value, 1); var y = WrapArg(value, 2); var z = WrapArg(value, 3); var w = WrapArg(value, 4);
        if (w?.Kind != SC2LuaBytecodeValueKind.Number || x?.Kind != SC2LuaBytecodeValueKind.Number ||
            y?.Kind != SC2LuaBytecodeValueKind.Number || z?.Kind != SC2LuaBytecodeValueKind.Number)
            return null;
        return new Quaternion((float)x.NumberValue, (float)y.NumberValue, (float)z.NumberValue, (float)w.NumberValue);
    }

    /// <summary>RECTANGLE(x1, z1, x2, z2).</summary>
    public static (float X1, float Z1, float X2, float Z2)? AsRectangle(SC2LuaBytecodeValue value)
    {
        if (WrapperOf(value) != "RECTANGLE") return null;
        var a = WrapArg(value, 1); var b = WrapArg(value, 2); var c = WrapArg(value, 3); var d = WrapArg(value, 4);
        if (a?.Kind != SC2LuaBytecodeValueKind.Number || b?.Kind != SC2LuaBytecodeValueKind.Number ||
            c?.Kind != SC2LuaBytecodeValueKind.Number || d?.Kind != SC2LuaBytecodeValueKind.Number)
            return null;
        return ((float)a.NumberValue, (float)b.NumberValue, (float)c.NumberValue, (float)d.NumberValue);
    }

    /// <summary>Scenario.Areas - a direct top-level child (confirmed
    /// against the real vanilla corpus: 4-space indent, sibling of Props/
    /// MasterChain/Chains/Armies).</summary>
    public static SC2LuaBytecodeValue FindAreas(SC2LuaBytecodeValue scenario) => DirectField(scenario, "Areas");

    /// <summary>Scenario.Chains - a direct top-level child.</summary>
    public static SC2LuaBytecodeValue FindChains(SC2LuaBytecodeValue scenario) => DirectField(scenario, "Chains");

    /// <summary>Scenario.Armies - a direct top-level child.</summary>
    public static SC2LuaBytecodeValue FindArmies(SC2LuaBytecodeValue scenario) => DirectField(scenario, "Armies");

    /// <summary>ScenarioInfo.StartPositions - a direct top-level child of
    /// _scenario.lua's own global table (a different global than
    /// _save.lua's "Scenario" - see the "version = 3 / ScenarioInfo = {...}"
    /// shape confirmed against the real corpus).</summary>
    public static SC2LuaBytecodeValue FindStartPositions(SC2LuaBytecodeValue scenarioInfo) => DirectField(scenarioInfo, "StartPositions");

    /// <summary>Scenario.MasterChain.&lt;chain-name&gt;.Markers - Markers
    /// lives one level deeper than Areas/Chains/Armies, nested under the
    /// map's single master chain entry (conventionally named
    /// "_MASTERCHAIN_", but scanned by content rather than hardcoded so any
    /// real chain name still resolves). Deliberately NOT a generic
    /// "search the whole tree for a field named Markers" - each entry in
    /// Scenario.Chains ALSO has its own "Markers" field (a plain array of
    /// marker-name strings, a completely different shape/purpose than this
    /// top-level marker dictionary) - confirmed via a real bytecode
    /// round-trip test that a naive whole-tree search can land on THAT
    /// decoy instead of the real table whenever a map has any chains,
    /// silently returning zero markers. Scoping the search to MasterChain's
    /// own immediate children only makes that collision structurally
    /// impossible.</summary>
    public static SC2LuaBytecodeValue FindMarkers(SC2LuaBytecodeValue scenario)
    {
        var masterChain = DirectField(scenario, "MasterChain");
        if (masterChain?.Kind != SC2LuaBytecodeValueKind.Table) return null;
        foreach (var chain in masterChain.TableValue.Fields.Values)
            if (chain?.Kind == SC2LuaBytecodeValueKind.Table && chain.TableValue.Fields.TryGetValue("Markers", out var markers))
                return markers;
        return null;
    }

    private static SC2LuaBytecodeValue DirectField(SC2LuaBytecodeValue table, string key) =>
        table?.Kind == SC2LuaBytecodeValueKind.Table && table.TableValue.Fields.TryGetValue(key, out var value) ? value : null;
}
