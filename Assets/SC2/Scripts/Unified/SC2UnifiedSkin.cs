using UnityEngine;

/// <summary>
/// Runtime-generated dark GUISkin matching the SC2 Game View top panel
/// (graphite background, rounded "pill" buttons, blue accent).
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
    // ---- Palette (kept in sync with SC2GameViewEditorUI) ----
    public static readonly Color Bg            = new Color(0.106f, 0.114f, 0.129f, 1f);
    // Kept in sync with SC2GameViewEditorUI.ColorSurface/ColorSurfaceHi/ColorBorder:
    // both were only ~0.04 above Bg, so buttons/fields barely read as distinct
    // controls against the dark background. Bumped up for clear contrast.
    public static readonly Color Surface       = new Color(0.208f, 0.220f, 0.251f, 1f);
    public static readonly Color SurfaceHi     = new Color(0.267f, 0.281f, 0.318f, 1f);
    public static readonly Color Accent        = new Color(0.357f, 0.576f, 0.929f, 1f);
    public static readonly Color AccentDim     = new Color(0.294f, 0.475f, 0.766f, 1f);
    public static readonly Color Border        = new Color(0.34f, 0.365f, 0.415f, 1f);
    public static readonly Color TextPrimary   = new Color(0.90f, 0.92f, 0.95f, 1f);
    // Also used by GUI.skin.box's textColor below, which is what the runtime
    // EditorGUILayout.HelpBox shim renders with (GUILayout.Label(msg,
    // GUI.skin.box)) — i.e. every "placeholder"/help/note box across every
    // tab. At (0.56, 0.60, 0.67) that read as blending into the dark
    // background; brightened to match SC2GameViewEditorUI.ColorTextSecondary.
    public static readonly Color TextSecondary = new Color(0.72f, 0.75f, 0.80f, 1f);

    // Scaled through SC2UiScale (View menu > UI Scale) instead of fixed
    // constants — see SC2UiScale's own doc comment for why this project
    // scales real layout numbers rather than a GUI.matrix transform.
    public static int FontSizeBody => SC2UiScale.F(11);
    public static int FontSizeMini => SC2UiScale.F(10);

    static SC2UnifiedSkin()
    {
        // Rebuild whenever the user changes UI Scale — Build() below is
        // otherwise only ever called lazily once per domain (see Ensure's
        // own "textures alive" sentinel comment), so without this a scale
        // change would need an assembly reload to actually take effect.
        SC2UiScale.Changed += () => { _skin = null; _pillGhost = null; };
    }

    private static GUISkin _skin;
    private static Texture2D _pillGhost;
    private static Texture2D _pillHover;
    private static Texture2D _pillActive;
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
        if (_skin != null && _pillGhost != null)
            return;

        Build();
    }

    private static void Build()
    {
        _pillGhost        = Rounded(24, 10, Surface, Border, 1f);
        _pillHover        = Rounded(24, 10, SurfaceHi, Border, 1f);
        _pillActive       = Rounded(24, 10, Accent, new Color(0, 0, 0, 0), 0f);
        _fieldNormal      = Rounded(20, 6, new Color(0.09f, 0.096f, 0.11f, 1f), Border, 1f);
        _fieldFocused     = Rounded(20, 6, new Color(0.09f, 0.096f, 0.11f, 1f), Accent, 1.25f);
        _toggleOff        = Rounded(16, 4, new Color(0.09f, 0.096f, 0.11f, 1f), Border, 1.25f);
        _toggleOffHover   = Rounded(16, 4, SurfaceHi, Border, 1.25f);
        _toggleOn         = Rounded(16, 4, Accent, new Color(0, 0, 0, 0), 0f);
        _sliderTrack      = Rounded(12, 5, new Color(0.09f, 0.096f, 0.11f, 1f), Border, 1f);
        _sliderThumb      = Rounded(16, 8, Accent, new Color(0, 0, 0, 0), 0f);
        _sliderThumbHover = Rounded(16, 8, AccentDim, new Color(0, 0, 0, 0), 0f);
        _scrollTrack      = Solid(new Color(0f, 0f, 0f, 0.15f));
        _scrollThumb      = Rounded(12, 5, SurfaceHi, new Color(0, 0, 0, 0), 0f);
        _card             = Rounded(28, 9, Surface, Border, 1.25f);

        var baseSkin = GUI.skin != null ? GUI.skin : ScriptableObject.CreateInstance<GUISkin>();
        _skin = Object.Instantiate(baseSkin);
        _skin.hideFlags = HideFlags.HideAndDontSave;
        _skin.name = "SC2UnifiedSkin";

        var pillBorder = new RectOffset(8, 8, 8, 8);
        // Padding/heights scaled alongside font size so a bigger font
        // doesn't end up visually cramped inside an unscaled pill/field —
        // border/corner-radius bake parameters above are left fixed
        // (cosmetic, reads fine unscaled at any of Scale's 1x-2x range).
        var buttonPadding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), SC2UiScale.Px(4), SC2UiScale.Px(4));
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
            alignment = TextAnchor.MiddleCenter,
            border = pillBorder,
            margin = new RectOffset(2, 2, 2, 2),
            padding = buttonPadding,
            normal   = { background = _pillGhost,  textColor = TextPrimary },
            hover    = { background = _pillHover,  textColor = TextPrimary },
            active   = { background = _pillActive, textColor = Color.white },
            focused  = { background = _pillHover,  textColor = TextPrimary },
            onNormal = { background = _pillActive, textColor = Color.white },
            onHover  = { background = _pillActive, textColor = Color.white },
            onActive = { background = _pillActive, textColor = Color.white }
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
            border = new RectOffset(4, 4, 4, 4),
            padding = togglePadding,
            margin = new RectOffset(2, 2, 2, 2),
            normal   = { background = _toggleOff,      textColor = TextSecondary },
            hover    = { background = _toggleOffHover, textColor = TextPrimary },
            active   = { background = _toggleOffHover, textColor = TextPrimary },
            focused  = { background = _toggleOff,      textColor = TextSecondary },
            onNormal = { background = _toggleOn,       textColor = TextPrimary },
            onHover  = { background = _toggleOn,       textColor = TextPrimary },
            onActive = { background = _toggleOn,       textColor = TextPrimary },
            onFocused = { background = _toggleOn,      textColor = TextPrimary }
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
