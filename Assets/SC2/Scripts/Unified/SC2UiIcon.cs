using System.Collections.Generic;
using UnityEngine;

/// <summary>Small bitmap icons drawn in place of a literal Unicode emoji character in a handful of
/// labels/buttons across the editor (the path lock toggle in SC2TerrainShell, and the section
/// icons in SC2InfoModelFieldDrawer.cs). Unity's legacy IMGUI text renderer cannot display real
/// emoji at all — the astral-plane character just rendered as nothing, and a follow-up attempt to
/// fix that by swapping in an OS emoji font (SC2EmojiFont, since removed) didn't just fail to show
/// anything either, it actively corrupted a neighbouring control's layout (a stale Layout-pass size
/// from before the dynamic font's glyphs had loaded, mismatched against the Repaint pass). Drawing
/// an actual Texture2D instead sidesteps both problems — it renders exactly the same regardless of
/// what font, if any, the OS provides.
///
/// Icons are Twemoji (https://twemoji.twitter.com, CC-BY 4.0), one PNG per Resources/EditorIcons/UI/
/// entry, named after the concept they stand in for (not the source codepoint) — see each call
/// site for which one is used where.</summary>
public static class SC2UiIcon
{
    private static readonly Dictionary<string, Texture2D> _cache = new();

    /// <summary>Null if `name` has no matching file under Resources/EditorIcons/UI — callers must
    /// treat that as "draw nothing", never throw, the same degrade-not-crash pattern already used
    /// for every other optional Resources.Load in this project.</summary>
    public static Texture2D Get(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // Re-check even on a cache hit for the same reason SC2EnvFieldGui.WhitePixel does: a
        // domain reload/Play Mode restart can null out a previously-loaded Object reference from
        // under a stale cache entry.
        if (_cache.TryGetValue(name, out var cached) && cached != null) return cached;
        var tex = Resources.Load<Texture2D>("EditorIcons/UI/" + name);
        _cache[name] = tex;
        return tex;
    }
}
