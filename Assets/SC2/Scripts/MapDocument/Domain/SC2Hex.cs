namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Convert.ToHexString (added in .NET 5) isn't available at this
    /// project's scripting API compatibility level — confirmed by a real
    /// Unity compile error (CS0117), not an assumption. This is the manual
    /// equivalent, shared by every content-hash call site instead of each
    /// reimplementing its own loop.
    /// </summary>
    public static class SC2Hex
    {
        public static string ToLowerHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = ToHexChar(b >> 4);
                chars[i * 2 + 1] = ToHexChar(b & 0xF);
            }
            return new string(chars);
        }

        private static char ToHexChar(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
    }
}
