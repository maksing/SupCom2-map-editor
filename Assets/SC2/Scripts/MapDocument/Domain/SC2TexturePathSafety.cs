using System;

namespace SC2.MapEditor.Document
{
    /// <summary>A texture path written into an exported BDF must be a game path
    /// ("/textures/..." or "/maps/..."). The editor's own bookkeeping markers ("file:C:/...",
    /// "archive:...") and machine-local Windows paths are meaningless to the game — the map
    /// fails to start — and they point at another user's disk when the map was made elsewhere.</summary>
    public static class SC2TexturePathSafety
    {
        /// <summary>True for an editor marker, a drive-letter path or a UNC path, also behind the slash a path from the texture library
        /// gets when it is turned into a game path ("/C:/...", "/archive:...").</summary>
        public static bool IsUnexportable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var p = path.TrimStart();
            if (p.StartsWith("\\\\", StringComparison.Ordinal)) return true;
            var bare = p.TrimStart('/', '\\');
            if (bare.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
            if (bare.StartsWith("archive:", StringComparison.OrdinalIgnoreCase)) return true;
            return bare.Length >= 3 && char.IsLetter(bare[0]) && bare[1] == ':' && (bare[2] == '\\' || bare[2] == '/');
        }
    }
}
