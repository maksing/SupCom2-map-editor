// SC2LuaBlocks.cs
// Minimal text utilities for working with regenerated _save.lua/
// _scenario.lua blocks. Those files are "Automatically generated code (do
// not edit)" with a very regular format: NOT a full Lua parser, just
// finding/replacing specific {...} blocks by brace-counting (accounting
// for '...' string literals so a '{'/'}' inside one doesn't throw off the
// balance — in practice none of the files examined have that, but it's
// handled anyway).

using System.Text;

public static class SC2LuaBlocks
{
    /// <summary>
    /// Finds the block <c>&lt;keyPattern&gt; {</c> ... its matching <c>}</c>.
    /// Returns (openBraceIndex, closeBraceIndex), or (-1,-1) if
    /// <paramref name="keyPattern"/> isn't found.
    /// </summary>
    public static (int Open, int Close) FindBlock(string text, string keyPattern, int from = 0)
    {
        var keyIdx = text.IndexOf(keyPattern, from);
        if (keyIdx < 0) return (-1, -1);

        var open = text.IndexOf('{', keyIdx);
        if (open < 0) return (-1, -1);

        var close = FindMatchingBrace(text, open);
        return (open, close);
    }

    /// <summary>Index of the '}' matching the '{' at <paramref name="openBrace"/>.</summary>
    public static int FindMatchingBrace(string text, int openBrace)
    {
        var depth = 0;
        var inString = false;
        for (var i = openBrace; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Replaces the block content [open+1, close) with <paramref name="newContent"/>.</summary>
    public static string ReplaceBlockContent(string text, int open, int close, string newContent)
    {
        var sb = new StringBuilder();
        sb.Append(text, 0, open + 1);
        sb.Append(newContent);
        sb.Append(text, close, text.Length - close);
        return sb.ToString();
    }
}
