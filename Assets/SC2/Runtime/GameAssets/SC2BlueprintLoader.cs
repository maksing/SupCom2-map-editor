using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KopiLua;
using lua_State = KopiLua.Lua.lua_State;
using CharPtr = KopiLua.Lua.CharPtr;

/// <summary>
/// Loads both development text blueprints and the Lua 5.1 bytecode blueprints
/// used by the shipped game assets. Bytecode is evaluated in a minimal Lua
/// state exposing only the two blueprint declaration functions.
/// </summary>
public static class SC2BlueprintLoader
{
    internal static readonly object SyncRoot = new object();
    private static readonly Dictionary<lua_State, CaptureContext> Captures = new Dictionary<lua_State, CaptureContext>();

    public static SC2Blueprint ParseFile(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return ParseBytes(File.ReadAllBytes(path), path);
    }

    public static SC2Blueprint ParseBytes(byte[] bytes, string sourcePath = null)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        if (!IsLua51Bytecode(bytes)) return SC2BlueprintParser.Parse(Encoding.UTF8.GetString(bytes), sourcePath);

        lock (SyncRoot)
        {
            var state = Lua.luaL_newstate();
            if (state == null) throw new InvalidOperationException("KopiLua luaL_newstate failed.");
            var capture = new CaptureContext(sourcePath);
            Captures.Add(state, capture);
            try
            {
                // No standard libraries: production blueprints only need a
                // declaration wrapper and literal tables, not filesystem/OS APIs.
                Lua.lua_register(state, "UnitBlueprint", CaptureUnitBlueprint);
                Lua.lua_register(state, "PropBlueprint", CapturePropBlueprint);

                var chars = new char[bytes.Length];
                for (int index = 0; index < bytes.Length; index++) chars[index] = (char)bytes[index];
                var buffer = new CharPtr(chars);
                if (Lua.luaL_loadbuffer(state, buffer, (uint)bytes.Length, sourcePath ?? "blueprint") != 0)
                    throw new InvalidDataException("Blueprint bytecode failed to load: " + Lua.lua_tostring(state, -1).ToString());
                if (Lua.lua_pcall(state, 0, 0, 0) != 0)
                    throw new InvalidDataException("Blueprint bytecode failed to execute: " + Lua.lua_tostring(state, -1).ToString());
                if (capture.Root == null)
                    throw new InvalidDataException("Blueprint bytecode did not call UnitBlueprint or PropBlueprint.");
                return new SC2Blueprint(capture.Declaration, sourcePath, capture.Root);
            }
            finally
            {
                Captures.Remove(state);
                Lua.lua_close(state);
            }
        }
    }

    public static bool IsLua51Bytecode(byte[] bytes)
    {
        return bytes != null && bytes.Length >= 5 && bytes[0] == 0x1b && bytes[1] == (byte)'L' && bytes[2] == (byte)'u' && bytes[3] == (byte)'a' && bytes[4] == 0x51;
    }

    private static int CaptureUnitBlueprint(lua_State state) => CaptureBlueprint(state, "UnitBlueprint");
    private static int CapturePropBlueprint(lua_State state) => CaptureBlueprint(state, "PropBlueprint");

    private static int CaptureBlueprint(lua_State state, string declaration)
    {
        CaptureContext context;
        if (!Captures.TryGetValue(state, out context)) throw new InvalidOperationException("No active blueprint capture context.");
        if (Lua.lua_type(state, 1) != Lua.LUA_TTABLE) throw new InvalidDataException(declaration + " expects a table argument.");
        if (context.Root != null) throw new InvalidDataException("Blueprint called more than one declaration wrapper.");
        context.Declaration = declaration;
        context.Root = ReadValue(state, 1, context, 0);
        return 0;
    }

    private static SC2BlueprintValue ReadValue(lua_State state, int index, CaptureContext context, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Blueprint table nesting exceeds 64 levels.");
        switch (Lua.lua_type(state, index))
        {
            case Lua.LUA_TNIL: return SC2BlueprintValue.Nil();
            case Lua.LUA_TBOOLEAN: return SC2BlueprintValue.Boolean(Lua.lua_toboolean(state, index) != 0);
            case Lua.LUA_TNUMBER: return SC2BlueprintValue.Number(Lua.lua_tonumber(state, index));
            case Lua.LUA_TSTRING: return SC2BlueprintValue.String(Lua.lua_tostring(state, index).ToString());
            case Lua.LUA_TTABLE: return ReadTable(state, index, context, depth + 1);
            default: throw new InvalidDataException("Blueprint contains unsupported Lua value type '" + Lua.lua_typename(state, Lua.lua_type(state, index)).ToString() + "'.");
        }
    }

    private static SC2BlueprintValue ReadTable(lua_State state, int index, CaptureContext context, int depth)
    {
        object identity = Lua.lua_topointer(state, index);
        if (identity == null || !context.ActiveTables.Add(identity)) throw new InvalidDataException("Blueprint contains a cyclic table reference.");
        try
        {
            var result = new SC2BlueprintTable();
            int itemCount = (int)Lua.lua_objlen(state, index);
            for (int item = 1; item <= itemCount; item++)
            {
                Lua.lua_rawgeti(state, index, item);
                result.Items.Add(ReadValue(state, Lua.lua_gettop(state), context, depth));
                Lua.lua_pop(state, 1);
            }

            Lua.lua_pushnil(state);
            while (Lua.lua_next(state, index) != 0)
            {
                int keyType = Lua.lua_type(state, -2);
                bool isSequentialItem = keyType == Lua.LUA_TNUMBER && IsSequentialItem(Lua.lua_tonumber(state, -2), itemCount);
                if (!isSequentialItem)
                {
                    string key = keyType == Lua.LUA_TSTRING
                        ? Lua.lua_tostring(state, -2).ToString()
                        : Lua.lua_tonumber(state, -2).ToString(CultureInfo.InvariantCulture);
                    result.Fields[key] = ReadValue(state, Lua.lua_gettop(state), context, depth);
                }
                Lua.lua_pop(state, 1); // Keep the key for lua_next.
            }
            return SC2BlueprintValue.Table(result);
        }
        finally { context.ActiveTables.Remove(identity); }
    }

    private static bool IsSequentialItem(float value, int itemCount)
    {
        int integer = (int)value;
        return value == integer && integer >= 1 && integer <= itemCount;
    }

    private sealed class CaptureContext
    {
        public readonly string SourcePath;
        public readonly HashSet<object> ActiveTables = new HashSet<object>();
        public string Declaration;
        public SC2BlueprintValue Root;
        public CaptureContext(string sourcePath) { SourcePath = sourcePath; }
    }
}
