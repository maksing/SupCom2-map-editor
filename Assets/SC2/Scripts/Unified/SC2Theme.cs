using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One editable colour of the interface palette.</summary>
public enum SC2ThemeColor
{
    Bg0, Bg1, Bg2, Bg3, Bg4,
    Line, LineStrong, SliderTrack,
    TextBright, TextPrimary, TextSecondary, TextDisabled,
    Accent, AccentHover, AccentPressed, AccentText, TextOnAccent, SelectedBorder,
    TabLine, TabGlowTop, TabGlowBottom, TabText,
    BrushRing, BrushRingDash,
    Cyan, Amber, Red, RedText, Violet, Yellow, Green, GreenText, AxisZ,
}

/// <summary>
/// The colours of the editor, in one place. The default palette follows Blender's dark interface: neutral greys from the dark
/// text-field wells (Bg0) up to the lit hover surface (Bg4), white text, and one muted blue accent for what is selected or filled.
/// Green, amber, red and the rest are for meaning only (status, warnings, delete, axes), never for chrome. The earlier blue-steel
/// palette stays available as the "Forge Steel" preset.
///
/// Every colour can be changed by the user (Customize > Interface Colors) and is kept per install in PlayerPrefs. The properties
/// below read the current value, so code that asks for <c>SC2Theme.Bg1</c> always gets the colour in force; anything that bakes a
/// colour into a texture or a style has to rebuild it when <see cref="RenderVersion"/> changes.
/// </summary>
public static class SC2Theme
{
    public readonly struct Entry
    {
        public readonly SC2ThemeColor Id;
        public readonly string Group;
        public readonly string Label;
        public readonly string Hint;
        public readonly Color Default;

        public Entry(SC2ThemeColor id, string group, string label, string hint, Color defaultColor)
        {
            Id = id; Group = group; Label = label; Hint = hint; Default = defaultColor;
        }
    }

    private const string PrefPrefix = "SC2.Theme.";

    private static Color Hex(uint rgb, byte alpha = 255) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);

    private static readonly Entry[] _entries =
    {
        new(SC2ThemeColor.Bg0, "Backgrounds", "Window", "The viewport frame, text fields and check boxes", Hex(0x1D1D1D)),
        new(SC2ThemeColor.Bg1, "Backgrounds", "Bars and cards", "Menu bar, main tabs and the cards inside panels", Hex(0x242424)),
        new(SC2ThemeColor.Bg2, "Backgrounds", "Side panels", "The panel behind the tool settings, and the sub-tabs", Hex(0x303030)),
        new(SC2ThemeColor.Bg3, "Backgrounds", "Buttons", "Buttons, chips and the resting state of controls", Hex(0x545454)),
        new(SC2ThemeColor.Bg4, "Backgrounds", "Hover", "Hovered controls and the active sub-tab", Hex(0x656565)),

        new(SC2ThemeColor.Line, "Lines", "Dividers", "Card borders and the lines between bars", Hex(0x1D1D1D)),
        new(SC2ThemeColor.LineStrong, "Lines", "Control borders", "Borders of buttons, fields and switches", Hex(0x3D3D3D)),
        new(SC2ThemeColor.SliderTrack, "Lines", "Slider track", "The empty part of a slider", Hex(0x454545)),

        new(SC2ThemeColor.TextBright, "Text", "Headings and values", "Titles, button labels and numbers", Color.white),
        new(SC2ThemeColor.TextPrimary, "Text", "Labels", "Field labels and the menu", Color.white),
        new(SC2ThemeColor.TextSecondary, "Text", "Hints", "Notes, captions and section headings", Color.white),
        new(SC2ThemeColor.TextDisabled, "Text", "Disabled text", "Text of controls that cannot be used right now", Color.white),

        new(SC2ThemeColor.Accent, "Accent", "Accent", "Slider fill, switches that are on, section ticks and the main action button", Hex(0x4772B3)),
        new(SC2ThemeColor.AccentHover, "Accent", "Accent hover", "The accent while the pointer is over it, and focused fields", Hex(0x5680C2)),
        new(SC2ThemeColor.AccentPressed, "Accent", "Accent pressed", "The accent while it is pressed", Hex(0x3A5F96)),
        new(SC2ThemeColor.AccentText, "Accent", "Selected text", "Text of the selected tool, row or option", Color.white),
        new(SC2ThemeColor.TextOnAccent, "Accent", "Text on accent", "Text drawn on top of an accent-coloured button", Color.white),
        new(SC2ThemeColor.SelectedBorder, "Accent", "Selection outline", "The outline of the selected tool, row or option", Hex(0x5680C2)),

        new(SC2ThemeColor.TabLine, "Tabs", "Active tab line", "The line under the active main tab", Hex(0x4772B3)),
        new(SC2ThemeColor.TabGlowTop, "Tabs", "Active tab glow (top)", "The top of the glow behind the active main tab", Hex(0x3D3D3D)),
        new(SC2ThemeColor.TabGlowBottom, "Tabs", "Active tab glow (bottom)", "The bottom of the glow behind the active main tab", Hex(0x4D4D4D)),
        new(SC2ThemeColor.TabText, "Tabs", "Inactive tab text", "Text of the main tabs that are not active", Color.white),

        new(SC2ThemeColor.BrushRing, "Viewport", "Brush ring", "The circle of a brush over the terrain", Hex(0xE6E6E6)),
        new(SC2ThemeColor.BrushRingDash, "Viewport", "Brush ring dashes", "The dashes of the brush ring", Hex(0xFFFFFF)),
        new(SC2ThemeColor.AxisZ, "Viewport", "Z axis", "The blue axis of the gizmo", Hex(0x4C8DFF)),

        new(SC2ThemeColor.Cyan, "Meaning", "Info", "The line under the active sub-tab and informational marks", Hex(0x5FB4E6)),
        new(SC2ThemeColor.Green, "Meaning", "Good", "Status dot, success marks", Hex(0x4ADE80)),
        new(SC2ThemeColor.GreenText, "Meaning", "Good text", "Text that reports success", Hex(0x6EE7A0)),
        new(SC2ThemeColor.Amber, "Meaning", "Warning", "Warnings and notices", Hex(0xFFA028)),
        new(SC2ThemeColor.Yellow, "Meaning", "Highlight", "Highlights and selection marks in the viewport", Hex(0xFFAA00)),
        new(SC2ThemeColor.Red, "Meaning", "Error", "Errors, delete buttons and the X axis", Hex(0xE5534B)),
        new(SC2ThemeColor.RedText, "Meaning", "Error text", "Text that reports a problem", Hex(0xF28B85)),
        new(SC2ThemeColor.Violet, "Meaning", "Other", "A spare accent for categories", Hex(0x9D8CFF)),
    };

    /// <summary>A named set of colours the Interface Colors window can apply in one click.</summary>
    public readonly struct Preset
    {
        public readonly string Name;
        private readonly Dictionary<SC2ThemeColor, Color> _overrides;

        public Preset(string name, Dictionary<SC2ThemeColor, Color> overrides)
        {
            Name = name; _overrides = overrides;
        }

        public Color ColorOf(SC2ThemeColor id, Color fallback) => _overrides != null && _overrides.TryGetValue(id, out var c) ? c : fallback;
    }

    private static readonly Preset[] _presets =
    {
        // The defaults above are the Blender look; a preset only lists what differs from them.
        new("Blender", null),
        new("Forge Steel", new Dictionary<SC2ThemeColor, Color>
        {
            [SC2ThemeColor.Bg0] = Hex(0x0A0E13),
            [SC2ThemeColor.Bg1] = Hex(0x0F141B),
            [SC2ThemeColor.Bg2] = Hex(0x141A23),
            [SC2ThemeColor.Bg3] = Hex(0x1A222D),
            [SC2ThemeColor.Bg4] = Hex(0x232D3B),
            [SC2ThemeColor.Line] = Hex(0x243040),
            [SC2ThemeColor.LineStrong] = Hex(0x33425A),
            [SC2ThemeColor.SliderTrack] = Hex(0x2B3849),
            [SC2ThemeColor.Accent] = Hex(0x2F6FED),
            [SC2ThemeColor.AccentHover] = Hex(0x4A86FF),
            [SC2ThemeColor.AccentPressed] = Hex(0x2358C4),
            [SC2ThemeColor.SelectedBorder] = Hex(0x4F8CFF),
            [SC2ThemeColor.TabLine] = Hex(0x446EB9),
            [SC2ThemeColor.TabGlowTop] = Hex(0x14203A),
            [SC2ThemeColor.TabGlowBottom] = Hex(0x152F4F),
            [SC2ThemeColor.BrushRing] = Hex(0x6FA2FF),
            [SC2ThemeColor.BrushRingDash] = Hex(0xAACCFF),
            [SC2ThemeColor.AxisZ] = Hex(0x4C8DFF),
            [SC2ThemeColor.Cyan] = Hex(0x4CC9F0),
            [SC2ThemeColor.Amber] = Hex(0xF2A93B),
            [SC2ThemeColor.Yellow] = Hex(0xF2D14B),
        }),
    };

    public static IReadOnlyList<Preset> Presets => _presets;

    /// <summary>Sets every colour of the palette to the preset's value (colours it does not list go back to the default).</summary>
    public static void ApplyPreset(Preset preset)
    {
        EnsureLoaded();
        foreach (var entry in _entries) Set(entry.Id, preset.ColorOf(entry.Id, entry.Default));
    }

    /// <summary>The preset whose colours match the palette exactly, or null when the user has mixed their own.</summary>
    public static string CurrentPresetName()
    {
        EnsureLoaded();
        foreach (var preset in _presets)
        {
            var same = true;
            foreach (var entry in _entries)
                if (Get(entry.Id) != preset.ColorOf(entry.Id, entry.Default)) { same = false; break; }
            if (same) return preset.Name;
        }
        return null;
    }

    private static Color[] _values;
    private static int _version;
    private static int _renderVersion;
    private static float _lastApplyTime = -10f;

    /// <summary>The palette, in the order the Interface Colors panel lists it.</summary>
    public static IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Rises on every change of any colour.</summary>
    public static int Version => _version;

    /// <summary>Fired when <see cref="RenderVersion"/> moves forward, i.e. when baked styles and textures should be rebuilt.</summary>
    public static event Action Applied;

    /// <summary>
    /// The version that baked styles compare against. It follows <see cref="Version"/>, but at most about seven times a second, so
    /// dragging a colour wheel does not rebuild every baked texture of the interface on every frame.
    /// </summary>
    public static int RenderVersion
    {
        get
        {
            if (_renderVersion != _version && Time.realtimeSinceStartup - _lastApplyTime >= 0.14f)
            {
                _renderVersion = _version;
                _lastApplyTime = Time.realtimeSinceStartup;
                Applied?.Invoke();
            }
            return _renderVersion;
        }
    }

    private static void EnsureLoaded()
    {
        if (_values != null) return;
        _values = new Color[_entries.Length];
        for (var i = 0; i < _entries.Length; i++)
        {
            _values[i] = _entries[i].Default;
            try
            {
                var saved = PlayerPrefs.GetString(PrefPrefix + _entries[i].Id, "");
                if (!string.IsNullOrEmpty(saved) && ColorUtility.TryParseHtmlString("#" + saved, out var parsed))
                    _values[i] = parsed;
            }
            catch (Exception)
            {
                // PlayerPrefs is not available (a headless test run, say): the defaults stay.
            }
        }
    }

    private static int IndexOf(SC2ThemeColor id)
    {
        for (var i = 0; i < _entries.Length; i++)
            if (_entries[i].Id == id) return i;
        return -1;
    }

    public static Color Get(SC2ThemeColor id)
    {
        EnsureLoaded();
        return _values[IndexOf(id)];
    }

    public static Color DefaultOf(SC2ThemeColor id) => _entries[IndexOf(id)].Default;

    public static bool IsCustom(SC2ThemeColor id) => Get(id) != DefaultOf(id);

    public static bool AnyCustom()
    {
        foreach (var entry in _entries)
            if (IsCustom(entry.Id)) return true;
        return false;
    }

    /// <summary>Sets a colour (opaque unless the original has an alpha) and keeps it for the next session.</summary>
    public static void Set(SC2ThemeColor id, Color color)
    {
        EnsureLoaded();
        var index = IndexOf(id);
        color.a = _entries[index].Default.a;
        if (_values[index] == color) return;
        _values[index] = color;
        Save(index);
        _version++;
    }

    public static void Reset(SC2ThemeColor id) => Set(id, DefaultOf(id));

    public static void ResetAll()
    {
        EnsureLoaded();
        foreach (var entry in _entries) Set(entry.Id, entry.Default);
    }

    private static void Save(int index)
    {
        try
        {
            var key = PrefPrefix + _entries[index].Id;
            if (_values[index] == _entries[index].Default) PlayerPrefs.DeleteKey(key);
            else PlayerPrefs.SetString(key, ColorUtility.ToHtmlStringRGBA(_values[index]));
            PlayerPrefs.Save();
        }
        catch (Exception)
        {
            // Not persisted; the colour still applies for this session.
        }
    }

    // ---- Surfaces ----
    /// <summary>Window and viewport.</summary>
    public static Color Bg0 => Get(SC2ThemeColor.Bg0);
    /// <summary>Menu bar and main tabs.</summary>
    public static Color Bg1 => Get(SC2ThemeColor.Bg1);
    /// <summary>Side panels and sub-tabs.</summary>
    public static Color Bg2 => Get(SC2ThemeColor.Bg2);
    /// <summary>Buttons, fields and chips.</summary>
    public static Color Bg3 => Get(SC2ThemeColor.Bg3);
    /// <summary>Hover, the active sub-tab.</summary>
    public static Color Bg4 => Get(SC2ThemeColor.Bg4);
    /// <summary>Card borders and dividers.</summary>
    public static Color Line => Get(SC2ThemeColor.Line);
    /// <summary>Control borders.</summary>
    public static Color LineStrong => Get(SC2ThemeColor.LineStrong);
    public static Color SliderTrack => Get(SC2ThemeColor.SliderTrack);

    // ---- Text ----
    /// <summary>Headings, values, button labels.</summary>
    public static Color TextBright => Get(SC2ThemeColor.TextBright);
    /// <summary>Labels and the menu.</summary>
    public static Color TextPrimary => Get(SC2ThemeColor.TextPrimary);
    /// <summary>Hints and inactive tabs.</summary>
    public static Color TextSecondary => Get(SC2ThemeColor.TextSecondary);
    public static Color TextDisabled => Get(SC2ThemeColor.TextDisabled);

    // ---- Accent (blue) ----
    public static Color Accent => Get(SC2ThemeColor.Accent);
    public static Color AccentHover => Get(SC2ThemeColor.AccentHover);
    public static Color AccentPressed => Get(SC2ThemeColor.AccentPressed);
    /// <summary>Text of active elements and the focus colour.</summary>
    public static Color AccentText => Get(SC2ThemeColor.AccentText);
    public static Color TextOnAccent => Get(SC2ThemeColor.TextOnAccent);
    /// <summary>Background of a selected element (translucent over the surface under it).</summary>
    public static Color SelectedFill { get { var c = Accent; c.a = 56f / 255f; return c; } }
    /// <summary>Outline of a selected element.</summary>
    public static Color SelectedBorder => Get(SC2ThemeColor.SelectedBorder);
    /// <summary>The line under the active main tab, and the glow above it.</summary>
    public static Color TabLine => Get(SC2ThemeColor.TabLine);
    public static Color TabGlowTop => Get(SC2ThemeColor.TabGlowTop);
    public static Color TabGlowBottom => Get(SC2ThemeColor.TabGlowBottom);
    /// <summary>Inactive tab text.</summary>
    public static Color TabText => Get(SC2ThemeColor.TabText);
    public static Color BrushRing => Get(SC2ThemeColor.BrushRing);
    public static Color BrushRingDash => Get(SC2ThemeColor.BrushRingDash);

    // ---- Meaning ----
    public static Color Cyan => Get(SC2ThemeColor.Cyan);
    public static Color Amber => Get(SC2ThemeColor.Amber);
    public static Color Red => Get(SC2ThemeColor.Red);
    public static Color RedText => Get(SC2ThemeColor.RedText);
    public static Color Violet => Get(SC2ThemeColor.Violet);
    public static Color Yellow => Get(SC2ThemeColor.Yellow);
    public static Color Green => Get(SC2ThemeColor.Green);
    public static Color GreenText => Get(SC2ThemeColor.GreenText);
    public static Color AxisZ => Get(SC2ThemeColor.AxisZ);

    /// <summary>The fill of a selected element, as the opaque colour it gives over a given surface (a baked texture cannot blend).</summary>
    public static Color SelectedFillOver(Color surface) => Color.Lerp(surface, Accent, 0.22f);

    /// <summary>The border of a selected element over a given surface.</summary>
    public static Color SelectedBorderOver(Color surface) => Color.Lerp(surface, SelectedBorder, 0.55f);

    // ---- Names the rest of the code already uses, mapped onto the palette ----
    public static Color Bg => Bg1;
    public static Color Panel => Bg2;
    /// <summary>Cards, rows and menus.</summary>
    public static Color Surface => Bg1;
    public static Color SurfaceHi => Bg3;
    public static Color Button => Bg3;
    public static Color ButtonHi => Bg4;
    public static Color Border => Line;
    public static Color Divider => Line;
    public static Color AccentDim => AccentPressed;
    /// <summary>The filled button for a tab's main action: the accent itself.</summary>
    public static Color Primary => Accent;
    public static Color PrimaryDim => AccentPressed;
    public static Color TextOnPrimary => TextOnAccent;
    /// <summary>Text fields, slider tracks and check boxes.</summary>
    public static Color FieldFill => Bg0;
    public static Color FieldBorder => LineStrong;
}
