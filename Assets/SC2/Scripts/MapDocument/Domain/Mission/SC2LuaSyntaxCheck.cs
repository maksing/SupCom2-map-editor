using System.Text;
using KopiLua;
using CharPtr = KopiLua.Lua.CharPtr;

namespace SC2.MapEditor.Document
{
    /// <summary>Compiles Lua text with KopiLua without running it, to tell a script the game would refuse to load from one it would.</summary>
    public static class SC2LuaSyntaxCheck
    {
        /// <summary>True when the text compiles; otherwise the compiler's message (with line number) is in error.</summary>
        public static bool TryCompile(string lua, out string error)
        {
            error = "";
            var bytes = Encoding.UTF8.GetBytes(lua ?? "");
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];

            var state = Lua.luaL_newstate();
            if (state == null)
            {
                error = "The Lua compiler could not start.";
                return false;
            }
            try
            {
                // Collecting while the parser runs makes the compile fail now and then on large scripts, and nothing is run here to need it.
                Lua.lua_gc(state, Lua.LUA_GCSTOP, 0);
                var code = Lua.luaL_loadbuffer(state, new CharPtr(chars), (uint)bytes.Length, "mission");
                if (code == 0) return true;
                // The message is missing when the compiler fails before it can push one, so it has a fallback text.
                var message = Lua.lua_tostring(state, -1);
                error = message == null ? "The script does not compile (code " + code + ")." : message.ToString();
                return false;
            }
            finally
            {
                Lua.lua_close(state);
            }
        }
    }
}
