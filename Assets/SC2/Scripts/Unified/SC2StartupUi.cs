using UnityEngine;

/// <summary>
/// The look of the start window: a dark rounded panel, green section headings, rows with a border, rounded buttons with a green
/// primary one and round "pills" for choices. Everything is plain GUIStyles over procedurally drawn rounded textures, so it looks
/// the same in the Editor Game view and in a build. The styles are rebuilt when the UI scale changes.
/// </summary>
public static class SC2StartupUi
{
    public static Color Green => SC2Theme.Accent;
    private static Color PanelFill => SC2Theme.Panel;
    private static Color PanelBorder => SC2Theme.LineStrong;
    private static Color RowFill => SC2Theme.Surface;
    private static Color RowBorder => SC2Theme.Line;
    private static Color ButtonFill => SC2Theme.Button;
    private static Color ButtonHover => SC2Theme.ButtonHi;
    private static Color ButtonActive => Color.Lerp(SC2Theme.Button, Color.black, 0.25f);
    private static Color PrimaryFill => SC2Theme.Primary;
    private static Color PrimaryHover => SC2Theme.AccentHover;
    private static Color PrimaryActive => SC2Theme.PrimaryDim;
    private static Color PillFill => SC2Theme.Surface;
    private static Color PillSelected => SC2Theme.Bg4;
    private static Color FieldFill => SC2Theme.FieldFill;
    private static Color FieldBorder => SC2Theme.FieldBorder;
    private static Color Text => SC2Theme.TextPrimary;
    private static Color TextDim => SC2Theme.TextSecondary;
    private static Color TextOnPrimary => SC2Theme.TextOnPrimary;

    public static GUIStyle Panel, Title, Section, Dim, DimMiddle, DimRight, Body, RowName, Row, Button, Primary, SizeButton, SizeButtonSelected, Pill, PillSelected_, Field, Link, FoldoutRow;

    private static float _builtForScale = -1f;
    private static int _builtThemeVersion = -1;
    private static Texture2D _arrowRight, _arrowDown, _rowHoverTex;
    private static Texture2D _panelTex, _rowTex, _buttonTex, _buttonHoverTex, _buttonActiveTex, _primaryTex, _primaryHoverTex, _primaryActiveTex,
        _sizeSelectedTex, _pillTex, _pillSelectedTex, _fieldTex, _fieldFocusTex;

    /// <summary>A rounded rectangle with a border, antialiased, small enough to be stretched as a 9-slice.</summary>
    public static Texture2D Rounded(Color fill, Color border, int radius, float borderWidth)
    {
        var size = radius * 2 + 2;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
        };
        var half = size * 0.5f;
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Signed distance to the rounded rectangle: negative inside.
                var qx = Mathf.Abs(x + 0.5f - half) - (half - radius);
                var qy = Mathf.Abs(y + 0.5f - half) - (half - radius);
                var distance = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                var coverage = Mathf.Clamp01(0.5f - distance);
                var borderMix = borderWidth <= 0f ? 0f : Mathf.Clamp01(0.5f + distance + borderWidth);
                var color = Color.Lerp(fill, border, borderMix);
                color.a *= coverage;
                pixels[y * size + x] = color;
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false);
        return texture;
    }

    /// <summary>A small white triangle (pointing right, or down), antialiased by supersampling; tinted when drawn.</summary>
    private static Texture2D Arrow(bool down)
    {
        const int size = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var covered = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        // Position in 0..1; for the down arrow the axes are swapped, so it is the same triangle turned.
                        var u = (x + (sx + 0.5f) / 4f) / size;
                        var v = (y + (sy + 0.5f) / 4f) / size;
                        if (down) { var t = u; u = 1f - v; v = t; }
                        // A triangle with its base on the left edge (u = 0.15) and its tip on the right (u = 0.9), centred on v = 0.5.
                        var along = (u - 0.15f) / 0.75f;
                        if (along >= 0f && along <= 1f && Mathf.Abs(v - 0.5f) <= 0.42f * (1f - along)) covered++;
                    }
                }
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(covered * 255 / 16));
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false);
        return texture;
    }

    private static Texture2D Tex(ref Texture2D texture, Color fill, Color border, int radius, float borderWidth)
    {
        if (texture == null) texture = Rounded(fill, border, radius, borderWidth);
        return texture;
    }

    private static GUIStyle Boxed(Texture2D normal, Texture2D hover, Texture2D active, int radius, Color text, int fontSize, FontStyle fontStyle)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1),
            alignment = TextAnchor.MiddleCenter,
            fontSize = fontSize,
            fontStyle = fontStyle,
            margin = new RectOffset(3, 3, 2, 2),
            padding = new RectOffset(10, 10, 4, 4),
            clipping = TextClipping.Clip,
        };
        foreach (var state in new[] { style.normal, style.focused, style.hover, style.active, style.onNormal, style.onFocused, style.onHover, style.onActive })
            state.textColor = text;
        style.normal.background = normal; style.focused.background = normal; style.onNormal.background = normal; style.onFocused.background = normal;
        style.hover.background = hover; style.onHover.background = hover;
        style.active.background = active; style.onActive.background = active;
        return style;
    }

    /// <summary>Builds (or rebuilds after a UI scale change) the styles. Call at the top of drawing the window.</summary>
    public static void EnsureStyles()
    {
        if (Panel != null && Mathf.Approximately(_builtForScale, SC2UiScale.Scale) && _builtThemeVersion == SC2Theme.RenderVersion && _panelTex != null && _buttonTex != null) return;
        _builtForScale = SC2UiScale.Scale;
        if (_builtThemeVersion != SC2Theme.RenderVersion)
        {
            // The baked panel, button and field textures carry palette colours; let them be baked again.
            foreach (var texture in new[] { _panelTex, _rowTex, _buttonTex, _buttonHoverTex, _buttonActiveTex, _primaryTex, _primaryHoverTex, _primaryActiveTex,
                         _sizeSelectedTex, _pillTex, _pillSelectedTex, _fieldTex, _fieldFocusTex, _rowHoverTex })
                if (texture != null) Object.Destroy(texture);
            _panelTex = _rowTex = _buttonTex = _buttonHoverTex = _buttonActiveTex = _primaryTex = _primaryHoverTex = _primaryActiveTex = null;
            _sizeSelectedTex = _pillTex = _pillSelectedTex = _fieldTex = _fieldFocusTex = _rowHoverTex = null;
            _builtThemeVersion = SC2Theme.RenderVersion;
        }

        var panelTex = Tex(ref _panelTex, PanelFill, PanelBorder, 10, 1f);
        var rowTex = Tex(ref _rowTex, RowFill, RowBorder, 6, 1f);
        var buttonTex = Tex(ref _buttonTex, ButtonFill, ButtonFill, 6, 0f);
        var buttonHoverTex = Tex(ref _buttonHoverTex, ButtonHover, ButtonHover, 6, 0f);
        var buttonActiveTex = Tex(ref _buttonActiveTex, ButtonActive, ButtonActive, 6, 0f);
        var primaryTex = Tex(ref _primaryTex, PrimaryFill, PrimaryFill, 6, 0f);
        var primaryHoverTex = Tex(ref _primaryHoverTex, PrimaryHover, PrimaryHover, 6, 0f);
        var primaryActiveTex = Tex(ref _primaryActiveTex, PrimaryActive, PrimaryActive, 6, 0f);
        var sizeSelectedTex = Tex(ref _sizeSelectedTex, ButtonFill, Green, 6, 2f);
        var pillTex = Tex(ref _pillTex, PillFill, PillFill, 12, 0f);
        var pillSelectedTex = Tex(ref _pillSelectedTex, PillSelected, PillSelected, 12, 0f);
        var fieldTex = Tex(ref _fieldTex, FieldFill, FieldBorder, 6, 1f);
        var fieldFocusTex = Tex(ref _fieldFocusTex, FieldFill, Green, 6, 1f);

        Panel = new GUIStyle(GUI.skin.box)
        {
            border = new RectOffset(11, 11, 11, 11),
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        Panel.normal.background = panelTex;

        Title = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(28), fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 6) };
        Title.normal.textColor = Text;

        Section = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(15), fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 12, 5) };
        Section.normal.textColor = Text;

        Dim = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(12), wordWrap = true, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
        Dim.normal.textColor = TextDim;

        DimMiddle = new GUIStyle(Dim) { alignment = TextAnchor.MiddleLeft };
        DimRight = new GUIStyle(Dim) { alignment = TextAnchor.MiddleRight, wordWrap = false, margin = new RectOffset(10, 10, 0, 0) };

        Body = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(13), wordWrap = true };
        Body.normal.textColor = Text;

        RowName = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(14), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
        RowName.normal.textColor = Text;

        Row = new GUIStyle(GUI.skin.box)
        {
            border = new RectOffset(7, 7, 7, 7),
            padding = new RectOffset(12, 10, 9, 9),
            margin = new RectOffset(0, 0, 3, 3),
        };
        Row.normal.background = rowTex;

        Button = Boxed(buttonTex, buttonHoverTex, buttonActiveTex, 6, Text, SC2UiScale.FRaw(13), FontStyle.Normal);
        Primary = Boxed(primaryTex, primaryHoverTex, primaryActiveTex, 6, TextOnPrimary, SC2UiScale.FRaw(13), FontStyle.Bold);
        SizeButton = Boxed(buttonTex, buttonHoverTex, buttonActiveTex, 6, Text, SC2UiScale.FRaw(13), FontStyle.Normal);
        SizeButtonSelected = Boxed(sizeSelectedTex, sizeSelectedTex, sizeSelectedTex, 6, Text, SC2UiScale.FRaw(13), FontStyle.Normal);
        Pill = Boxed(pillTex, pillSelectedTex, pillTex, 12, Text, SC2UiScale.FRaw(12), FontStyle.Normal);
        PillSelected_ = Boxed(pillSelectedTex, pillSelectedTex, pillSelectedTex, 12, Text, SC2UiScale.FRaw(12), FontStyle.Bold);

        Field = new GUIStyle(GUI.skin.textField)
        {
            border = new RectOffset(7, 7, 7, 7),
            padding = new RectOffset(11, 11, 6, 6),
            margin = new RectOffset(3, 3, 2, 2),
            fontSize = SC2UiScale.FRaw(13),
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        };
        Field.normal.background = fieldTex; Field.hover.background = fieldTex;
        Field.focused.background = fieldFocusTex; Field.active.background = fieldFocusTex;
        foreach (var state in new[] { Field.normal, Field.hover, Field.focused, Field.active }) state.textColor = Text;

        var rowHoverTex = Tex(ref _rowHoverTex, SC2Theme.Bg2, RowBorder, 6, 1f);
        FoldoutRow = new GUIStyle(GUI.skin.button)
        {
            border = new RectOffset(7, 7, 7, 7),
            alignment = TextAnchor.MiddleLeft,
            fontSize = SC2UiScale.FRaw(14),
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(38, 10, 6, 6),
            margin = new RectOffset(0, 0, 3, 3),
        };
        foreach (var state in new[] { FoldoutRow.normal, FoldoutRow.focused, FoldoutRow.hover, FoldoutRow.active, FoldoutRow.onNormal, FoldoutRow.onFocused, FoldoutRow.onHover, FoldoutRow.onActive })
            state.textColor = Text;
        FoldoutRow.normal.background = rowTex; FoldoutRow.focused.background = rowTex; FoldoutRow.onNormal.background = rowTex; FoldoutRow.onFocused.background = rowTex;
        FoldoutRow.hover.background = rowHoverTex; FoldoutRow.onHover.background = rowHoverTex;
        FoldoutRow.active.background = rowHoverTex; FoldoutRow.onActive.background = rowHoverTex;

        Link = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.FRaw(12), margin = new RectOffset(0, 0, 8, 0) };
        Link.normal.textColor = TextDim; Link.hover.textColor = Text;
    }

    public static void SectionHeading(string text) => GUILayout.Label(text, Section);

    public static float ButtonHeight => SC2UiScale.PxfRaw(30f);

    public static bool PrimaryButton(string label, float width) =>
        GUILayout.Button(label, Primary, GUILayout.Width(SC2UiScale.PxfRaw(width)), GUILayout.Height(ButtonHeight));

    public static bool PlainButton(string label, float width) =>
        GUILayout.Button(label, Button, GUILayout.Width(SC2UiScale.PxfRaw(width)), GUILayout.Height(ButtonHeight));

    /// <summary>A button as wide as its text, for the small actions on a row.</summary>
    public static bool FitButton(string label, bool primary = false) =>
        GUILayout.Button(label, primary ? Primary : Button, GUILayout.Height(ButtonHeight));

    /// <summary>A row that opens and closes the block under it, with an arrow that points right when closed and down when open.
    /// Returns the new state.</summary>
    public static bool Foldout(ref bool expanded, string label)
    {
        if (_arrowRight == null) _arrowRight = Arrow(false);
        if (_arrowDown == null) _arrowDown = Arrow(true);

        var clicked = GUILayout.Button(label, FoldoutRow, GUILayout.Height(SC2UiScale.PxfRaw(38f)));
        var rect = GUILayoutUtility.GetLastRect();
        if (clicked) expanded = !expanded;
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            var size = SC2UiScale.PxfRaw(14f);
            var previous = GUI.color;
            GUI.color = new Color(Text.r, Text.g, Text.b, previous.a);
            GUI.DrawTexture(new Rect(rect.x + SC2UiScale.PxfRaw(14f), rect.center.y - size * 0.5f, size, size), expanded ? _arrowDown : _arrowRight);
            GUI.color = previous;
        }
        return expanded;
    }

    /// <summary>A text field with a placeholder shown while it is empty.</summary>
    public static string TextField(string value, string placeholder, params GUILayoutOption[] options)
    {
        var result = GUILayout.TextField(value ?? "", Field, options);
        if (string.IsNullOrEmpty(result) && Event.current != null && Event.current.type == EventType.Repaint)
        {
            var rect = GUILayoutUtility.GetLastRect();
            var previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            GUI.Label(new Rect(rect.x + Field.padding.left, rect.y, rect.width - Field.padding.horizontal, rect.height), placeholder, Dim);
            GUI.color = previous;
        }
        return result;
    }

    /// <summary>A label centred vertically in a row; with <paramref name="expand"/> it takes the free width, otherwise only what it needs.</summary>
    public static void CenteredLabel(string text, GUIStyle style, bool expand = true)
    {
        GUILayout.BeginVertical(expand ? new[] { GUILayout.ExpandWidth(true) } : new GUILayoutOption[0]);
        GUILayout.FlexibleSpace();
        GUILayout.Label(text, style);
        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
    }

    /// <summary>Starts a row's right-hand group of buttons, centred vertically in the row.</summary>
    public static void BeginRowActions()
    {
        GUILayout.BeginVertical();
        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
    }

    public static void EndRowActions()
    {
        GUILayout.EndHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
    }
}
