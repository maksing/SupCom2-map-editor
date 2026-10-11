using UnityEngine;

/// <summary>
/// Runtime-generated dark GUISkin matching the SC2 Game View top panel
/// (Forge Steel palette: dark blue-grey surfaces, rounded buttons, blue accent; see SC2Theme).
///
/// Applied by SC2GameViewEditorUI around all overlay drawing, so every
/// GUILayout.Button / TextField / Toggle / HorizontalSlider used anywhere
/// in the embedded terrain editor and inspectors automatically picks up
/// the unified look — no need to restyle dozens of individual call sites.
///
/// All textures are baked at runtime with HideFlags.HideAndDontSave and the
/// skin is rebuilt automatically if Unity destroys them (domain reload,
/// play-mode transitions), which was a source of "grey/blank UI" bugs in
/// long editor sessions.
/// </summary>
public static class SC2UnifiedSkin
{
    // ---- Palette (SC2Theme; SC2GameViewEditorUI reads the same values) ----
    public static Color Bg => SC2Theme.Bg;
    // Surface/SurfaceHi are the button fills; the cards are SC2Theme.Surface.
    public static Color Surface => SC2Theme.Button;
    public static Color SurfaceHi => SC2Theme.ButtonHi;
    public static Color Accent => SC2Theme.Accent;
    public static Color AccentDim => SC2Theme.AccentPressed;
    public static Color Border => SC2Theme.LineStrong;
    public static Color TextPrimary => SC2Theme.TextPrimary;
    // Also used by GUI.skin.box's textColor below, which is what the runtime
    // EditorGUILayout.HelpBox shim renders with (GUILayout.Label(msg,
    // GUI.skin.box)) — i.e. every "placeholder"/help/note box across every tab.
    public static Color TextSecondary => SC2Theme.TextSecondary;

    // Scaled through SC2UiScale (View menu > UI Scale) instead of fixed
    // constants — see SC2UiScale's own doc comment for why this project
    // scales real layout numbers rather than a GUI.matrix transform.
    public static int FontSizeBody => SC2Type.Body;
    public static int FontSizeMini => SC2Type.Caption;

    static SC2UnifiedSkin()
    {
        // Rebuild whenever the user changes UI Scale — Build() below is
        // otherwise only ever called lazily once per domain (see Ensure's
        // own "textures alive" sentinel comment), so without this a scale
        // change would need an assembly reload to actually take effect.
        SC2UiScale.Changed += () => { _skin = null; _pillGhost = null; };
    }

    private static GUISkin _skin;
    private static int _builtThemeVersion = -1;
    private static Texture2D _pillGhost;
    private static Texture2D _pillHover;
    private static Texture2D _pillActive;
    private static Texture2D _pillSelected;
    private static Texture2D _fieldNormal;
    private static Texture2D _fieldFocused;
    private static Texture2D _toggleOff;
    private static Texture2D _toggleOffHover;
    private static Texture2D _toggleOn;
    private static Texture2D _sliderTrack;
    private static Texture2D _sliderThumb;
    private static Texture2D _sliderThumbHover;
    private static Texture2D _scrollTrack;
    private static Texture2D _scrollThumb;
    private static Texture2D _card;
    private static Texture2D _radioOff;
    private static Texture2D _radioOffHover;
    private static Texture2D _radioOn;
    private static GUIStyle _selectRow;
    private static GUIStyle _radio;
    private static GUIStyle _smallButton;
    private static GUIStyle _iconButton;

    /// <summary>A button that holds only a symbol (a reset arrow, a cross): no side padding, so the glyph is centred in a narrow button
    /// instead of being pushed to one side of it.</summary>
    public static GUIStyle IconButtonStyle { get { Ensure(); return _iconButton; } }

    /// <summary>A compact button for dense rows (filter chips): caption-sized text and tight padding.</summary>
    public static GUIStyle SmallButtonStyle { get { Ensure(); return _smallButton; } }

    /// <summary>A row the user picks from a list (armies, zones, units): a left-aligned pill that is outlined in the accent while it is
    /// the selected one. Use it with GUILayout.Toggle where the state means "this row is the current one", not an on/off option.</summary>
    public static GUIStyle SelectRowStyle { get { Ensure(); return _selectRow; } }

    /// <summary>A round one-of-many selector with no label of its own (the row's text sits beside it).</summary>
    public static GUIStyle RadioStyle { get { Ensure(); return _radio; } }

    /// <summary>9-sliced rounded card texture, shared with panel boxes.</summary>
    public static Texture2D CardTexture { get { Ensure(); return _card; } }
    /// <summary>Border used by rounded cards (matches CardTexture bake radius).</summary>
    public static readonly RectOffset CardBorder = new RectOffset(9, 9, 9, 9);

    /// <summary>
    /// Returns the unified skin, rebuilding it if any baked texture has been
    /// destroyed by Unity (assembly reload / play-mode enter-exit).
    /// </summary>
    public static GUISkin Get()
    {
        Ensure();
        return _skin;
    }

    private static void Ensure()
    {
        // _pillGhost doubles as the "textures alive" sentinel: if Unity killed
        // the baked textures, every style in the skin is pointing at dead
        // objects and the whole thing must be rebuilt, not patched.
        if (_skin != null && _pillGhost != null && _builtThemeVersion == SC2Theme.RenderVersion)
            return;

        Build();
    }

    private static void Build()
    {
        _builtThemeVersion = SC2Theme.RenderVersion;
        _pillGhost        = Rounded(24, 10, Surface, Border, 1f);
        _pillHover        = Rounded(24, 10, SurfaceHi, Border, 1f);
        _pillActive       = Rounded(24, 10, SC2Theme.AccentPressed, new Color(0, 0, 0, 0), 0f);
        _pillSelected     = Rounded(24, 10, SC2Theme.SelectedFillOver(Surface), SC2Theme.SelectedBorderOver(Surface), 1.25f);
        _fieldNormal      = Rounded(20, 6, SC2Theme.FieldFill, Border, 1f);
        _fieldFocused     = Rounded(20, 6, SC2Theme.FieldFill, SC2Theme.AccentHover, 1.25f);
        MeasureSwitch();
        _toggleOff        = SwitchTexture(on: false, hover: false);
        _toggleOffHover   = SwitchTexture(on: false, hover: true);
        _toggleOn         = SwitchTexture(on: true, hover: false);
        _sliderTrack      = Rounded(12, 5, SC2Theme.SliderTrack, SC2Theme.SliderTrack, 0f);
        _sliderThumb      = Rounded(16, 8, Accent, Color.white, 1.5f);
        _sliderThumbHover = Rounded(16, 8, SC2Theme.AccentHover, Color.white, 1.5f);
        _scrollTrack      = Solid(new Color(0f, 0f, 0f, 0.15f));
        _scrollThumb      = Rounded(12, 5, SC2Theme.LineStrong, new Color(0, 0, 0, 0), 0f);
        _card             = Rounded(28, 9, SC2Theme.Surface, SC2Theme.Line, 1.25f);
        _radioOff         = RadioTexture(on: false, hover: false);
        _radioOffHover    = RadioTexture(on: false, hover: true);
        _radioOn          = RadioTexture(on: true, hover: false);

        var baseSkin = GUI.skin != null ? GUI.skin : ScriptableObject.CreateInstance<GUISkin>();
        _skin = Object.Instantiate(baseSkin);
        _skin.hideFlags = HideFlags.HideAndDontSave;
        _skin.name = "SC2UnifiedSkin";

        var pillBorder = new RectOffset(8, 8, 8, 8);
        // Padding/heights scaled alongside font size so a bigger font
        // doesn't end up visually cramped inside an unscaled pill/field —
        // border/corner-radius bake parameters above are left fixed
        // (cosmetic, reads fine unscaled at any of Scale's 1x-2x range).
        var buttonPadding = new RectOffset(SC2UiScale.Px(6), SC2UiScale.Px(6), SC2UiScale.Px(5), SC2UiScale.Px(5));
        var fieldPadding = new RectOffset(SC2UiScale.Px(7), SC2UiScale.Px(7), SC2UiScale.Px(3), SC2UiScale.Px(3));
        var togglePadding = new RectOffset(SC2UiScale.Px(20), SC2UiScale.Px(4), SC2UiScale.Px(2), SC2UiScale.Px(2));
        var boxPadding = new RectOffset(SC2UiScale.Px(8), SC2UiScale.Px(8), SC2UiScale.Px(6), SC2UiScale.Px(6));

        // Every named style below is built from a fresh new GUIStyle(), not
        // new GUIStyle(_skin.button) etc. copy-constructed from the base skin.
        // That copy-construction used to be here, and it's a real Unity trap:
        // it throws NullReferenceException if the source style is a plain C#
        // null (every named style on a bare GUISkin from
        // ScriptableObject.CreateInstance — the fallback above when GUI.skin
        // was itself null — is actually null, not just empty) or if it holds
        // a destroyed-object reference (a font/texture killed by a domain
        // reload). Every property below is fully overridden anyway, so
        // starting blank is both safe and visually identical.
        //
        // Button — same "ghost pill" as the header tabs, so panel buttons no
        // longer look like the light-grey legacy IMGUI skin.
        _skin.button = new GUIStyle()
        {
            fontSize = FontSizeBody,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            // Text that does not fit is cut at the button's edge instead of spilling out of it.
            clipping = TextClipping.Clip,
            border = pillBorder,
            margin = new RectOffset(2, 2, 2, 2),
            padding = buttonPadding,
            normal   = { background = _pillGhost,  textColor = SC2Theme.TextBright },
            hover    = { background = _pillHover,  textColor = SC2Theme.TextBright },
            active   = { background = _pillActive, textColor = SC2Theme.TextOnAccent },
            focused  = { background = _pillHover,  textColor = SC2Theme.TextBright },
            onNormal = { background = _pillSelected, textColor = SC2Theme.AccentText },
            onHover  = { background = _pillSelected, textColor = SC2Theme.AccentText },
            onActive = { background = _pillSelected, textColor = SC2Theme.AccentText }
        };

        _skin.label = new GUIStyle()
        {
            fontSize = FontSizeBody,
            normal = { textColor = TextPrimary }
        };

        _skin.textField = new GUIStyle()
        {
            fontSize = FontSizeBody,
            alignment = TextAnchor.MiddleLeft,
            border = new RectOffset(5, 5, 5, 5),
            padding = fieldPadding,
            margin = new RectOffset(2, 2, 2, 2),
            normal  = { background = _fieldNormal,  textColor = TextPrimary },
            hover   = { background = _fieldNormal,  textColor = TextPrimary },
            focused = { background = _fieldFocused, textColor = TextPrimary },
            active  = { background = _fieldFocused, textColor = TextPrimary }
        };
        _skin.textArea = new GUIStyle(_skin.textField) { wordWrap = true };

        // Toggle — the stock skin draws a light checkbox sprite that clashes
        // with the dark theme; replace with a flat rounded square that fills
        // with the accent color when on.
        _skin.toggle = new GUIStyle()
        {
            fontSize = FontSizeBody,
            // The first line of the label sits level with the switch; a label that wraps to more lines grows the control downwards.
            alignment = TextAnchor.UpperLeft,
            // A label longer than the control is wide wraps instead of running out of it.
            wordWrap = true,
            // The switch is baked into the upper left of the texture and kept unstretched by the left and top borders; the rest
            // of the control (the label) stays transparent. The texture has one more, clear row under the switch, which is what a
            // taller control (a label on several lines) stretches, so the switch keeps its shape and stays at the top.
            border = new RectOffset(_switchLeftBorder, _switchRightBorder, _switchHeight, 0),
            // The control has to be at least as tall as the switch texture (kept unstretched by its top and bottom borders), or
            // Unity squeezes the borders and the switch comes out flat.
            padding = new RectOffset(_switchLeftBorder + SC2UiScale.Px(6), SC2UiScale.Px(4), SwitchVerticalPadding(), SwitchVerticalPadding()),
            margin = new RectOffset(2, 2, 2, 2),
            normal   = { background = _toggleOff,      textColor = SC2Theme.TextPrimary },
            hover    = { background = _toggleOffHover, textColor = SC2Theme.TextBright },
            active   = { background = _toggleOffHover, textColor = SC2Theme.TextBright },
            focused  = { background = _toggleOff,      textColor = SC2Theme.TextPrimary },
            onNormal = { background = _toggleOn,       textColor = SC2Theme.TextBright },
            onHover  = { background = _toggleOn,       textColor = SC2Theme.TextBright },
            onActive = { background = _toggleOn,       textColor = SC2Theme.TextBright },
            onFocused = { background = _toggleOn,      textColor = SC2Theme.TextBright }
        };

        _selectRow = new GUIStyle(_skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Normal,
            clipping = TextClipping.Clip,
            wordWrap = false,
            padding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), SC2UiScale.Px(4), SC2UiScale.Px(4)),
        };
        _selectRow.normal.textColor = SC2Theme.TextPrimary;
        _selectRow.onNormal.textColor = SC2Theme.AccentText;
        _selectRow.onHover.textColor = SC2Theme.AccentText;
        _selectRow.onActive.textColor = SC2Theme.AccentText;

        _smallButton = new GUIStyle(_skin.button)
        {
            fontSize = FontSizeMini,
            padding = new RectOffset(SC2UiScale.Px(3), SC2UiScale.Px(3), SC2UiScale.Px(2), SC2UiScale.Px(2)),
        };

        _iconButton = new GUIStyle(_skin.button)
        {
            padding = new RectOffset(0, 0, 0, 0),
            clipping = TextClipping.Overflow,
        };

        _radio = new GUIStyle()
        {
            fixedWidth = SC2UiScale.Pxf(20f),
            fixedHeight = SC2UiScale.Pxf(20f),
            margin = new RectOffset(2, 2, 2, 2),
            normal    = { background = _radioOff },
            hover     = { background = _radioOffHover },
            active    = { background = _radioOffHover },
            focused   = { background = _radioOff },
            onNormal  = { background = _radioOn },
            onHover   = { background = _radioOn },
            onActive  = { background = _radioOn },
            onFocused = { background = _radioOn }
        };

        _skin.horizontalSlider = new GUIStyle()
        {
            fixedHeight = SC2UiScale.Pxf(14f),
            border = new RectOffset(4, 4, 4, 4),
            normal = { background = _sliderTrack }
        };
        _skin.horizontalSliderThumb = new GUIStyle()
        {
            fixedWidth = SC2UiScale.Pxf(14f),
            fixedHeight = SC2UiScale.Pxf(14f),
            border = new RectOffset(7, 7, 7, 7),
            normal = { background = _sliderThumb },
            hover  = { background = _sliderThumbHover },
            active = { background = _sliderThumbHover }
        };

        StyleScrollbar(_skin.verticalScrollbar, _skin.verticalScrollbarThumb, vertical: true);
        StyleScrollbar(_skin.horizontalScrollbar, _skin.horizontalScrollbarThumb, vertical: false);
        // Hide the arrow buttons entirely — modern slim scrollbar look.
        _skin.verticalScrollbarUpButton = GUIStyle.none;
        _skin.verticalScrollbarDownButton = GUIStyle.none;
        _skin.horizontalScrollbarLeftButton = GUIStyle.none;
        _skin.horizontalScrollbarRightButton = GUIStyle.none;

        _skin.box = new GUIStyle()
        {
            fontSize = FontSizeMini,
            // Help and note texts are drawn as labels in this style: they wrap to the width they are given.
            wordWrap = true,
            border = CardBorder,
            padding = boxPadding,
            normal = { background = _card, textColor = TextSecondary }
        };
    }

    private static void StyleScrollbar(GUIStyle bar, GUIStyle thumb, bool vertical)
    {
        if (bar != null)
        {
            if (vertical) bar.fixedWidth = SC2UiScale.Pxf(10f); else bar.fixedHeight = SC2UiScale.Pxf(10f);
            bar.border = new RectOffset(0, 0, 0, 0);
            bar.normal.background = _scrollTrack;
        }
        if (thumb != null)
        {
            if (vertical) thumb.fixedWidth = SC2UiScale.Pxf(10f); else thumb.fixedHeight = SC2UiScale.Pxf(10f);
            thumb.border = new RectOffset(4, 4, 4, 4);
            thumb.normal.background = _scrollThumb;
            thumb.hover.background = _scrollThumb;
            thumb.active.background = _scrollThumb;
        }
    }

    // The switch is baked at its real size, so it follows UI Scale (the skin is rebuilt when the scale changes). Its track is
    // 38 x 18 design pixels, about twice as long as high.
    private const float SwitchTrackDesignWidth = 38f, SwitchTrackDesignHeight = 18f;
    private static int _switchTrackWidth, _switchTrackHeight, _switchPad, _switchGap;
    private static int _switchWidth, _switchHeight, _switchLeftBorder, _switchRightBorder;

    private static void MeasureSwitch()
    {
        _switchTrackWidth = Mathf.Max(20, SC2UiScale.Px((int)SwitchTrackDesignWidth));
        _switchTrackHeight = Mathf.Max(10, SC2UiScale.Px((int)SwitchTrackDesignHeight));
        _switchPad = Mathf.Max(1, SC2UiScale.Px(2));
        _switchGap = Mathf.Max(2, SC2UiScale.Px(4));
        _switchRightBorder = 2;
        _switchLeftBorder = _switchPad + _switchTrackWidth + _switchGap;
        _switchWidth = _switchLeftBorder + 6 + _switchRightBorder;
        _switchHeight = _switchTrackHeight + 2 * _switchPad;
        if ((_switchHeight & 1) != 0) _switchHeight++;
    }

    /// <summary>Vertical padding that makes a toggle at least as tall as the switch texture.</summary>
    private static int SwitchVerticalPadding()
    {
        var line = Mathf.CeilToInt(FontSizeBody * 1.3f);
        return Mathf.Max(SC2UiScale.Px(5), Mathf.CeilToInt((_switchHeight - line) * 0.5f) + 1);
    }

    /// <summary>The on/off switch of a toggle: a pill with a knob at the left of a texture whose rest is clear (see MeasureSwitch for sizes).</summary>
    private static Texture2D SwitchTexture(bool on, bool hover)
    {
        int width = _switchWidth, height = _switchHeight + 1;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var trackFill = on ? (hover ? SC2Theme.AccentHover : Accent) : (hover ? SC2Theme.Bg1 : SC2Theme.FieldFill);
        var trackBorder = on ? trackFill : SC2Theme.LineStrong;
        var knobColor = on ? Color.white : SC2Theme.TextSecondary;
        float trackLeft = _switchPad, trackWidth = _switchTrackWidth, trackHeight = _switchTrackHeight;
        // Row 0 (the bottom one, clear) is the stretching row; the switch fills the rows above it.
        var trackBottom = 1f + (_switchHeight - trackHeight) * 0.5f;
        var knobInset = Mathf.Max(2f, trackHeight * 0.16f);
        var knobRadius = trackHeight * 0.5f - knobInset;
        var knobCenterX = on ? trackLeft + trackWidth - trackHeight * 0.5f : trackLeft + trackHeight * 0.5f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var px = x + 0.5f; var py = y + 0.5f;
                // Signed distance to the pill-shaped track (a capsule whose radius is half its height).
                var cx = trackLeft + trackWidth * 0.5f; var cy = trackBottom + trackHeight * 0.5f;
                var radius = trackHeight * 0.5f;
                var halfLength = trackWidth * 0.5f - radius;
                var ex = Mathf.Max(Mathf.Abs(px - cx) - halfLength, 0f); var ey = py - cy;
                var d = Mathf.Sqrt(ex * ex + ey * ey) - radius;
                var coverage = Mathf.Clamp01(0.5f - d);
                var color = Color.Lerp(trackFill, trackBorder, Mathf.Clamp01(0.5f + d + 1f));
                color.a *= coverage;
                // The knob on top.
                var kd = Mathf.Sqrt((px - knobCenterX) * (px - knobCenterX) + (py - cy) * (py - cy)) - knobRadius;
                var kc = Mathf.Clamp01(0.5f - kd);
                if (kc > 0f)
                {
                    color = Color.Lerp(color, knobColor, kc);
                    color.a = Mathf.Max(color.a, kc);
                }
                tex.SetPixel(x, y, color);
            }
        }
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>A round selector: a ring, with a filled centre in the accent when it is the chosen one.</summary>
    private static Texture2D RadioTexture(bool on, bool hover)
    {
        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var ringColor = on ? Accent : (hover ? SC2Theme.AccentHover : SC2Theme.LineStrong);
        var fill = hover && !on ? SC2Theme.Bg1 : SC2Theme.FieldFill;
        const float center = size * 0.5f, outer = size * 0.5f - 2f, ringWidth = 2.5f, dot = size * 0.2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Mathf.Sqrt((x + 0.5f - center) * (x + 0.5f - center) + (y + 0.5f - center) * (y + 0.5f - center));
                var coverage = Mathf.Clamp01(outer - d + 0.5f);
                var color = Color.Lerp(fill, ringColor, Mathf.Clamp01(d - (outer - ringWidth) + 0.5f));
                if (on)
                    color = Color.Lerp(color, Accent, Mathf.Clamp01(dot - d + 0.5f));
                color.a = coverage;
                tex.SetPixel(x, y, color);
            }
        }
        tex.Apply(false, true);
        return tex;
    }

    private static Texture2D Solid(Color color)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        t.SetPixel(0, 0, color);
        t.Apply(false, true);
        return t;
    }

    /// <summary>Anti-aliased rounded rect with optional 1px border, for 9-slicing.</summary>
    private static Texture2D Rounded(int size, float radius, Color fill, Color border, float borderWidth)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var hasBorder = borderWidth > 0.01f && border.a > 0.001f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = RoundedDist(x + 0.5f, y + 0.5f, size, radius);
            var coverage = Mathf.Clamp01(0.5f - d);
            var pixel = fill;
            if (hasBorder && d > -borderWidth)
            {
                var edgeT = Mathf.Clamp01((d + borderWidth) / borderWidth);
                pixel = Color.Lerp(fill, border, edgeT);
            }
            pixel.a *= coverage;
            tex.SetPixel(x, y, pixel);
        }

        tex.Apply(false, true);
        return tex;
    }

    private static float RoundedDist(float x, float y, float size, float radius)
    {
        var half = size * 0.5f;
        var dx = Mathf.Abs(x - half) - (half - radius);
        var dy = Mathf.Abs(y - half) - (half - radius);
        var ax = Mathf.Max(dx, 0f);
        var ay = Mathf.Max(dy, 0f);
        var outside = Mathf.Sqrt(ax * ax + ay * ay) - radius;
        var inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
        return outside + inside;
    }
}
