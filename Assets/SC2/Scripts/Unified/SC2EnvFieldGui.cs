using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Self-contained IMGUI controls for SC2EnvironmentShell (label, numeric
/// field, slider, toggle, foldout, RGB/HSV color picker), built ONLY from
/// plain UnityEngine GUI/GUILayout calls — never EditorGUILayout/EditorStyles.
///
/// Why: EditorStyles.xxx resolves to the REAL Unity Editor inspector skin
/// when compiled with UNITY_EDITOR, but to a bare GUI.skin fallback in the
/// UnityEditorRuntimeShim used by a standalone Build & Run player — two
/// different fonts/colors/widgets for the same call. Plain GUI/GUILayout
/// calls always use the engine's runtime GUISkin, in the Editor's Game view
/// (Play Mode) AND in an actual build alike, so a control built only from
/// these primitives renders pixel-identical in both — which is the whole
/// point of this class existing instead of reusing EditorGUILayout directly.
/// </summary>
public static class SC2EnvFieldGui
{
    private static readonly Dictionary<string, string> _text = new();
    private static readonly Dictionary<string, float> _lastValue = new();
    private static readonly Dictionary<string, int> _lastIntValue = new();
    private static readonly Dictionary<string, bool> _colorExpanded = new();

    // Persisted per-field HSV state for the color picker. Color.RGBToHSV is
    // lossy at black/white/gray (any hue/saturation maps to the same RGB
    // once value=0, so re-deriving h/s from the RGB every frame snapped the
    // wheel back to hue=0/sat=0 the instant the color was black — which is
    // exactly SunColor's starting value — making the wheel look like it did
    // nothing and the whole picker look like it "reset" after every edit).
    // _lastColorValue records the color WE last produced; if the incoming
    // value still matches it, the color hasn't changed for any reason other
    // than our own last frame's edit, so the persisted (h,s,v,intensity)
    // keeps being used as-is instead of being re-derived from a lossy RGB.
    private static readonly Dictionary<string, Color> _lastColorValue = new();
    private static readonly Dictionary<string, (float h, float s, float v, float intensity)> _hsvState = new();

    private static bool ColorsApproxEqual(Color a, Color b) =>
        Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) &&
        Mathf.Approximately(a.b, b.b) && Mathf.Approximately(a.a, b.a);

    // Deliberately NOT cached in a static field: a cached GUIStyle can hold a
    // reference to a background texture/font that a later domain reload or
    // Play Mode restart destroys (SC2UnifiedSkin rebuilds its baked textures
    // when that happens — see its own Ensure()/_pillGhost check), silently
    // rendering nothing or throwing NullReferenceException out of GUILayout
    // and wiping the whole panel for the frame (caught by the try/catch
    // around DrawEmbeddedGUI, which then shows only the error box — i.e.
    // "every field/slider disappeared"). SC2TerrainShell.SafeGuiStyle
    // works around the exact same hazard the exact same way: build a fresh
    // GUIStyle every call instead of reusing one across frames.
    private static GUIStyle SafeStyle(GUIStyle preferred, int fontSize)
    {
        GUIStyle result = null;
        if (preferred != null)
        {
            try { result = new GUIStyle(preferred); }
            catch (System.NullReferenceException) { result = null; }
        }
        result ??= new GUIStyle();
        result.fontSize = fontSize;
        return result;
    }

    private static GUIStyle Label => SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Body);

    private static GUIStyle BoldLabel
    {
        get
        {
            var s = SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Body);
            s.fontStyle = FontStyle.Bold;
            return s;
        }
    }

    private static GUIStyle MiniLabel
    {
        get
        {
            var s = SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Caption);
            s.normal.textColor = SC2Theme.TextSecondary;
            return s;
        }
    }

    private static GUIStyle WrapLabelStyle
    {
        get
        {
            var s = SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Body);
            s.wordWrap = true;
            return s;
        }
    }

    /// <summary>Long/dynamic-content hint labels overflow the panel instead of
    /// wrapping when drawn with a plain GUILayout.Label (no width, no
    /// wordWrap) — the panel is a fixed-width embedded column, not a window
    /// that grows to fit content. Use this instead of GUILayout.Label for any
    /// label whose text length varies with data/state.</summary>
    public static void WrapLabel(string text, float width)
    {
        GUILayout.Label(text, WrapLabelStyle, GUILayout.Width(Mathf.Max(20f, width)));
    }

    private static GUIStyle TextFieldStyle => SafeStyle(GUI.skin != null ? GUI.skin.textField : null, SC2Type.Body);

    /// <summary>The numeric boxes show their value at the right edge, as in the reference.</summary>
    private static GUIStyle NumberFieldStyle
    {
        get
        {
            var style = TextFieldStyle;
            style.alignment = TextAnchor.MiddleRight;
            return style;
        }
    }

    private static Texture2D _whitePixel;
    private static Texture2D WhitePixel
    {
        get
        {
            if (_whitePixel == null)
            {
                _whitePixel = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _whitePixel.SetPixel(0, 0, Color.white);
                _whitePixel.Apply(false, false);
            }
            return _whitePixel;
        }
    }

    private static Color BorderColor => SC2Theme.LineStrong;

    private static void FillRect(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, WhitePixel);
        GUI.color = old;
    }

    private static void DrawBorder(Rect r, Color c)
    {
        FillRect(new Rect(r.x, r.y, r.width, 1f), c);
        FillRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
        FillRect(new Rect(r.x, r.y, 1f, r.height), c);
        FillRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
    }

    private static string FormatFloat(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---- Section header / foldout ----
    public static void SectionLabel(string text) => SectionLabel(null, text);

    /// <summary>iconName is an SC2UiIcon name (Resources/EditorIcons/UI/&lt;name&gt;.png) drawn
    /// immediately before the text — see SC2InfoModelFieldDrawer.cs for every real use. Null/empty
    /// draws no icon, same as the plain SectionLabel(text) overload above.</summary>
    public static void SectionLabel(string iconName, string text)
    {
        var tex = SC2UiIcon.Get(iconName);
        if (tex == null) { SC2ForgeUi.Section(text); return; }
        GUILayout.BeginHorizontal();
        var size = SC2UiScale.Pxf(20f);
        GUILayout.Label(tex, GUILayout.Width(size), GUILayout.Height(size));
        GUILayout.Space(4);
        GUILayout.Label(text, BoldLabel);
        GUILayout.EndHorizontal();
    }

    public static bool Foldout(ref bool expanded, string label) => Foldout(null, ref expanded, label);

    /// <summary>Same iconName convention as SectionLabel above. Drawn in a fixed Rect (not
    /// GUILayout flow) between the row's own arrow and its label text, since the whole row
    /// (background fill + click detection) already has to be one explicit Rect.</summary>
    public static bool Foldout(string iconName, ref bool expanded, string label)
    {
        var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(SC2UiScale.Px(20)), GUILayout.ExpandWidth(true));
        // A header bar a step lighter than the panel under it, like the buttons, instead of a darker hole in it.
        FillRect(rect, rect.Contains(Event.current.mousePosition) ? SC2Theme.Bg3 : Color.Lerp(SC2Theme.Bg2, SC2Theme.Bg3, 0.5f));
        var arrow = expanded ? "▼" : "▶";
        var boldLabel = BoldLabel;
        boldLabel.alignment = TextAnchor.MiddleLeft;
        var textX = rect.x + 4f;
        var tex = SC2UiIcon.Get(iconName);
        if (tex != null)
        {
            var arrowWidth = SC2UiScale.Pxf(16f);
            var iconSize = SC2UiScale.Pxf(17f);
            GUI.Label(new Rect(textX, rect.y, arrowWidth, rect.height), arrow, boldLabel);
            GUI.DrawTexture(new Rect(textX + arrowWidth, rect.y + (rect.height - iconSize) * 0.5f, iconSize, iconSize), tex);
            textX += arrowWidth + iconSize + 4f;
            GUI.Label(new Rect(textX, rect.y, rect.xMax - textX - 4f, rect.height), label, boldLabel);
        }
        else
        {
            GUI.Label(new Rect(textX, rect.y, rect.width - 8f, rect.height), arrow + "  " + label, boldLabel);
        }

        var e = Event.current;
        if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
        {
            expanded = !expanded;
            GUI.changed = true;
            e.Use();
        }
        return expanded;
    }

    public static bool Button(string label, params GUILayoutOption[] options) => GUILayout.Button(label, options);

    /// <summary>The colour of the buttons that do a tab's main work (export, bake, generate): the primary green of the editor theme.</summary>
    public static Color PrimaryActionColor => SC2Theme.Primary;

    /// <summary>A button for one of the main actions of a tab: the filled green, rounded button of the start window, so it stands out
    /// from the rest.</summary>
    public static bool PrimaryButton(string label, params GUILayoutOption[] options) => PrimaryButton(label, GUI.skin.button, options);

    public static bool PrimaryButton(string label, GUIStyle style, params GUILayoutOption[] options)
    {
        SC2StartupUi.EnsureStyles();
        // The look is the start window's primary button; the text size follows the button style the caller would have used.
        var primary = new GUIStyle(SC2StartupUi.Primary);
        if (style != null && style.fontSize > 0) primary.fontSize = style.fontSize;
        // A stretching button takes the width of the whole scroll view; keep it inside the panel's content, margins included.
        var buttonOptions = new GUILayoutOption[options.Length + 1];
        buttonOptions[0] = GUILayout.MaxWidth(Mathf.Max(1f, SC2TerrainShell.GetUnifiedContentWidth() - 6f));
        options.CopyTo(buttonOptions, 1);
        return GUILayout.Button(label, primary, buttonOptions);
    }

    /// <summary>True while the numeric box for `key` holds keyboard focus.
    /// Every RawXBox* helper below skips its own "incoming value changed
    /// elsewhere, reformat the displayed text" check while focused — found
    /// via a real report: SC2ObjectsShell's Area edge fields defer actually
    /// applying an edit until the field loses focus, so the box's own
    /// `value` argument (recomputed fresh from data each frame) stays
    /// frozen while the user types. Without this check, RawXBox's `_lastValue`
    /// bookkeeping (updated to the just-parsed PARTIAL value on every single
    /// keystroke, unconditionally, whether or not any caller actually acted
    /// on it) drifted out of sync with that frozen `value` after exactly one
    /// keystroke, and the very next repaint saw a "mismatch" and snapped the
    /// display straight back to the pre-edit number — reading as "a value
    /// set by dragging in the viewport can't be typed over at all."</summary>
    /// <summary>Lets a click elsewhere take the caret out of a field, and Enter or Esc leave it. Unity never moves the keyboard focus away
    /// from a text field by itself, and the editor's "click missed the field" release (SC2TerrainShell.ClearFocusIfClickMissedField) only
    /// knows the fields that tell it where they are: a field that did not left the caret in place for good, with every hotkey and the
    /// marker moves that wait for the keyboard switched off until the field was clicked out of some other way.</summary>
    private static void TrackFocus(string controlName, Rect rect)
    {
        var e = Event.current;
        if (e == null || e.type == EventType.Layout) return;
        SC2TerrainShell.RegisterFocusedFieldRect(controlName, rect);
        if (GUI.GetNameOfFocusedControl() == controlName && (e.type == EventType.KeyDown || e.type == EventType.Used) &&
            (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.Escape))
            GUI.FocusControl(null);
    }

    private static bool IsFieldFocused(string key) => GUI.GetNameOfFocusedControl() == "SC2EnvNum_" + key;

    // ---- Numeric fields ----
    public static float FloatField(string label, float value, string key)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Label);
        GUILayout.FlexibleSpace();
        value = RawFloatBox(value, key, 64f);
        GUILayout.EndHorizontal();
        return value;
    }

    public static int IntField(string label, int value, string key) => Mathf.RoundToInt(FloatField(label, value, key));

    public static bool Toggle(string label, bool value)
    {
        GUILayout.BeginHorizontal();
        var height = SC2UiScale.Pxf(16f);
        var switchRect = GUILayoutUtility.GetRect(height * 1.8f, height, GUILayout.Width(height * 1.8f), GUILayout.Height(height));
        GUILayout.Space(6f);
        GUILayout.Label(label, Label);
        GUILayout.EndHorizontal();

        var e = Event.current;
        SC2ForgeUi.DrawSwitch(switchRect, value, switchRect.Contains(e.mousePosition));
        if (e.type == EventType.MouseDown && switchRect.Contains(e.mousePosition))
        {
            value = !value;
            GUI.changed = true;
            e.Use();
        }
        return value;
    }

    public static string TextField(string label, string value, string key)
    {
        GUILayout.Label(label, Label);
        GUI.SetNextControlName("SC2EnvText_" + key);
        var typed = GUILayout.TextField(value ?? "", TextFieldStyle);
        TrackFocus("SC2EnvText_" + key, GUILayoutUtility.GetLastRect());
        return typed;
    }

    /// <summary>Label on its own line, then a horizontal-drag slider paired
    /// with a live numeric box — matches the Costs/Paint panel's
    /// "Radius/Strength/Hardness" sliders (screenshot reference), the one
    /// style everyone already recognizes as "looks right".
    ///
    /// [min,max] bounds the drag handle itself, but not a manually-typed
    /// number in the paired box — the range is a convenient drag scale, not
    /// field validation, so the user can still type a value outside it.
    /// Reverted per direction: an earlier hard-clamp pass here (also
    /// clamping manual entry) was undone.
    ///
    /// Reserves the whole row as ONE explicit rect via GUILayoutUtility
    /// (the same technique Foldout/SectionLabel already use reliably) and
    /// draws the slider/box within manually-computed sub-rects, instead of
    /// a nested BeginHorizontal + implicit-width HorizontalSlider — the
    /// latter let the slider's drag handle bleed a few pixels past the
    /// panel's real right edge once this row started rendering inside the
    /// scroll view added to the embedded panel (GUILayout's expand-to-fill
    /// sizing for a slider control isn't reliable across that extra layout
    /// nesting level, the same class of issue MoveXY hit earlier). A single
    /// GetRect call's width is unambiguous regardless of what contains it.</summary>
    public static float Slider(string label, float value, float min, float max, string key)
    {
        GUILayout.Label(label, Label);
        var row = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.ExpandWidth(true));
        var boxWidth = SC2UiScale.Pxf(52f);
        var gap = SC2UiScale.Pxf(6f);
        var sliderRect = new Rect(row.x, row.y + 1f, Mathf.Max(1f, row.width - boxWidth - gap), SC2UiScale.Pxf(16f));
        var boxRect = new Rect(sliderRect.xMax + gap, row.y, boxWidth, row.height);

        var clamped = Mathf.Clamp(value, min, max);
        var sliderValue = SC2ForgeUi.Slider(sliderRect, clamped, min, max);
        var draggedBySlider = !Mathf.Approximately(sliderValue, clamped);
        // Bug fixed here: the box's staleness check must compare against the
        // real incoming `value` (which may legitimately sit outside
        // [min,max] from manual entry) when the slider itself isn't being
        // dragged — comparing against `sliderValue` (always clamped) instead
        // made a manually-typed out-of-range number visually snap back to
        // the clamped value one frame after typing, even though this method
        // was supposed to leave it unclamped. Confirmed via a real report:
        // a typed value appeared to "get clamped" despite the earlier
        // hard-clamp revert.
        var boxValue = RawFloatBoxAt(boxRect, draggedBySlider ? sliderValue : value, key);

        return draggedBySlider ? sliderValue : boxValue;
    }

    /// <summary>Same shape as Slider, discrete-stepped for int fields (every
    /// field should have a slider except checkboxes/toggles and color
    /// pickers, per direction — including int fields, which get a rounded/
    /// stepped slider rather than a plain number box). Same soft-bounded
    /// contract as the float Slider — [min,max] only scales the drag
    /// handle, manual entry isn't clamped. Same explicit-rect reasoning as
    /// Slider above.</summary>
    public static int IntSlider(string label, int value, int min, int max, string key)
    {
        GUILayout.Label(label, Label);
        var row = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.ExpandWidth(true));
        var boxWidth = SC2UiScale.Pxf(52f);
        var gap = SC2UiScale.Pxf(6f);
        var sliderRect = new Rect(row.x, row.y + 1f, Mathf.Max(1f, row.width - boxWidth - gap), SC2UiScale.Pxf(16f));
        var boxRect = new Rect(sliderRect.xMax + gap, row.y, boxWidth, row.height);

        var clamped = Mathf.Clamp(value, min, max);
        var sliderValue = Mathf.RoundToInt(SC2ForgeUi.Slider(sliderRect, clamped, min, max));
        var draggedBySlider = sliderValue != clamped;
        // Same fix as Slider above — compare against the real incoming
        // `value`, not the clamped `sliderValue`, when not dragging.
        var boxValue = RawIntBoxAt(boxRect, draggedBySlider ? sliderValue : value, key);

        return draggedBySlider ? sliderValue : boxValue;
    }

    private static float RawFloatBoxAt(Rect rect, float value, string key)
    {
        var hasText = _text.TryGetValue(key, out var text);
        // While this control holds keyboard focus, never overwrite the
        // user's in-progress typed text with a freshly-formatted `value` —
        // see IsFieldFocused's own doc comment for the desync this
        // otherwise causes for any caller that defers applying an edit
        // until the field loses focus (SC2ObjectsShell's Area edge fields).
        if (!hasText || (!IsFieldFocused(key) && (!_lastValue.TryGetValue(key, out var lastVal) || !Mathf.Approximately(lastVal, value))))
            text = FormatFloat(value);

        // Was unnamed, unlike TextField above — left Unity to auto-assign a
        // control ID, which shifts whenever a conditional element earlier in
        // the same layout appears/disappears (e.g. SC2DecalsShell.DrawEdit's
        // "Apply properties" button, which only exists once the draft
        // differs from the committed value — i.e. the instant you type a
        // new number here). Without a stable name, after typing a value the
        // field would keep keyboard focus and nothing else (clicking a
        // decal in the viewport, G/R/S) would work afterward. A stable,
        // key-derived name
        // is the same fix already established elsewhere in this codebase
        // for this exact ID-instability bug class.
        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUI.TextField(rect, text, NumberFieldStyle);
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, rect);

        var parsedOk = float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        var result = parsedOk ? parsed : value;
        _lastValue[key] = result;
        return result;
    }

    private static int RawIntBoxAt(Rect rect, int value, string key)
    {
        var hasText = _text.TryGetValue(key, out var text);
        if (!hasText || (!IsFieldFocused(key) && (!_lastIntValue.TryGetValue(key, out var lastVal) || lastVal != value)))
            text = value.ToString(CultureInfo.InvariantCulture);

        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUI.TextField(rect, text, NumberFieldStyle);
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, rect);

        var parsedOk = int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
        var result = parsedOk ? parsed : value;
        _lastIntValue[key] = result;
        return result;
    }

    private static int RawIntBox(int value, string key, float width)
    {
        var hasText = _text.TryGetValue(key, out var text);
        if (!hasText || (!IsFieldFocused(key) && (!_lastIntValue.TryGetValue(key, out var lastVal) || lastVal != value)))
            text = value.ToString(CultureInfo.InvariantCulture);

        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUILayout.TextField(text, NumberFieldStyle, GUILayout.Width(width));
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, GUILayoutUtility.GetLastRect());

        var parsedOk = int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed);
        var result = parsedOk ? parsed : value;
        _lastIntValue[key] = result;
        return result;
    }

    private static float RawFloatBox(float value, string key, float width)
    {
        var hasText = _text.TryGetValue(key, out var text);
        if (!hasText || (!IsFieldFocused(key) && (!_lastValue.TryGetValue(key, out var lastVal) || !Mathf.Approximately(lastVal, value))))
            text = FormatFloat(value);

        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUILayout.TextField(text, NumberFieldStyle, GUILayout.Width(width));
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, GUILayoutUtility.GetLastRect());

        var parsedOk = float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        var result = parsedOk ? parsed : value;
        _lastValue[key] = result;
        return result;
    }

    private static float AxisField(string axis, float value, string key)
    {
        GUILayout.Label(axis, MiniLabel, GUILayout.Width(SC2UiScale.Pxf(12f)));
        return RawFloatBoxExpand(value, key);
    }

    private static float RawFloatBoxExpand(float value, string key)
    {
        var hasText = _text.TryGetValue(key, out var text);
        if (!hasText || (!IsFieldFocused(key) && (!_lastValue.TryGetValue(key, out var lastVal) || !Mathf.Approximately(lastVal, value))))
            text = FormatFloat(value);

        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUILayout.TextField(text, NumberFieldStyle, GUILayout.MinWidth(30f), GUILayout.ExpandWidth(true));
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, GUILayoutUtility.GetLastRect());

        var parsedOk = float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        var result = parsedOk ? parsed : value;
        _lastValue[key] = result;
        return result;
    }

    public static Vector2 Vector2Field(string label, Vector2 v, string key)
    {
        GUILayout.Label(label, Label);
        GUILayout.BeginHorizontal();
        v.x = AxisField("X", v.x, key + ":x");
        v.y = AxisField("Y", v.y, key + ":y");
        GUILayout.EndHorizontal();
        return v;
    }

    public static Vector3 Vector3Field(string label, Vector3 v, string key)
    {
        GUILayout.Label(label, Label);
        GUILayout.BeginHorizontal();
        v.x = AxisField("X", v.x, key + ":x");
        v.y = AxisField("Y", v.y, key + ":y");
        v.z = AxisField("Z", v.z, key + ":z");
        GUILayout.EndHorizontal();
        return v;
    }

    public static Vector4 Vector4Field(string label, Vector4 v, string key)
    {
        GUILayout.Label(label, Label);
        GUILayout.BeginHorizontal();
        v.x = AxisField("X", v.x, key + ":x");
        v.y = AxisField("Y", v.y, key + ":y");
        v.z = AxisField("Z", v.z, key + ":z");
        v.w = AxisField("W", v.w, key + ":w");
        GUILayout.EndHorizontal();
        return v;
    }

    // ---- Full RGB/HSV color picker ----
    // hdr=true (only SunColor today) additionally shows an "Intensity" row:
    // the wheel/value-bar always operate on a gamut-normalized 0..1 color
    // (like every real HSV color wheel), and HDR overbright is layered on
    // top as a separate multiplier — the same split real HDR color fields
    // use (pick the hue/tint on the wheel, dial brightness past 1 with a
    // separate control) rather than trying to cram unbounded values onto a
    // 0..1 square.
    public static Color ColorField(string label, Color value, string key, bool hdr = false, bool showAlpha = true, bool startExpanded = true)
    {
        // Defaults to expanded (not a click-to-reveal foldout) so the full
        // RGB/HSV picker is visible immediately — the whole point of
        // replacing the old fixed-swatch palette was to make a real picker
        // the normal, always-available state, not something hidden behind an
        // extra click.
        if (!_colorExpanded.TryGetValue(key, out var expanded))
            expanded = startExpanded;

        GUILayout.BeginHorizontal();
        var swatchW = SC2UiScale.Pxf(24f); var swatchH = SC2UiScale.Pxf(20f);
        var swatchRect = GUILayoutUtility.GetRect(swatchW, swatchH, GUILayout.Width(swatchW), GUILayout.Height(swatchH));
        FillRect(swatchRect, new Color(Mathf.Clamp01(value.r), Mathf.Clamp01(value.g), Mathf.Clamp01(value.b), 1f));
        DrawBorder(swatchRect, BorderColor);
        GUILayout.Space(6f);
        GUILayout.Label(label, Label);
        GUILayout.EndHorizontal();

        var e = Event.current;
        if (e.type == EventType.MouseDown && swatchRect.Contains(e.mousePosition))
        {
            expanded = !expanded;
            _colorExpanded[key] = expanded;
            GUI.changed = true;
            e.Use();
        }

        if (!expanded)
            return value;

        GUILayout.BeginVertical();
        try
        {
        float h, s, v, intensity;
        if (!_lastColorValue.TryGetValue(key, out var lastColor) || !ColorsApproxEqual(lastColor, value))
        {
            // `value` changed for a reason other than our own last frame's
            // wheel/bar/channel edit (external Undo/Redo/Reset, or first
            // load) — this is the only point where we're allowed to derive
            // h/s/v from the RGB, lossy as that is at black/white/gray.
            RederiveHsv(value, hdr, out h, out s, out v, out intensity);
        }
        else
        {
            (h, s, v, intensity) = _hsvState.TryGetValue(key, out var st) ? st : (0f, 0f, 0f, 1f);
        }

        // try/finally: if anything in here throws, GUILayout's Begin/End
        // group stack still must balance for the frame (an unclosed
        // BeginHorizontal aborts every remaining control this OnGUI call —
        // which is what "the whole panel disappeared after editing a
        // channel" looked like). LogException surfaces the real cause in
        // the console instead of the failure being silently swallowed by
        // the try/catch around DrawEmbeddedGUI further up the call chain.
        GUILayout.BeginHorizontal();
        try
        {
            var wheelSize = SC2UiScale.Pxf(108f);
            var wheelRect = GUILayoutUtility.GetRect(wheelSize, wheelSize, GUILayout.Width(wheelSize), GUILayout.Height(wheelSize));
            GUI.DrawTexture(wheelRect, GetWheelTexture());
            DrawWheelMarker(wheelRect, h, s);
            if (DragInWheel(wheelRect, out var wheelHue, out var wheelSat))
            {
                h = wheelHue;
                s = wheelSat;
            }

            GUILayout.Space(8f);
            var valueBarWidth = SC2UiScale.Pxf(20f);
            var valueRect = GUILayoutUtility.GetRect(valueBarWidth, wheelSize, GUILayout.Width(valueBarWidth), GUILayout.Height(wheelSize));
            GUI.DrawTexture(valueRect, GetValueBarTexture(h, s));
            DrawBorder(valueRect, BorderColor);
            DrawValueBarMarker(valueRect, v);
            if (DragInRect(valueRect, out var valueT))
                v = 1f - valueT.y;
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            GUILayout.EndHorizontal();
        }

        if (hdr)
            intensity = ChannelRow("Intensity", intensity, key + ":intensity");

        var rgb = Color.HSVToRGB(h, s, v);
        value = hdr
            ? new Color(rgb.r * intensity, rgb.g * intensity, rgb.b * intensity, value.a)
            : new Color(rgb.r, rgb.g, rgb.b, value.a);

        GUILayout.Space(4f);
        if (!_channelModeHsv.TryGetValue(key, out var hsvMode))
            hsvMode = false;
        GUILayout.BeginHorizontal();
        if (SegmentButton("RGB", !hsvMode)) { hsvMode = false; _channelModeHsv[key] = false; }
        if (SegmentButton("HSV", hsvMode)) { hsvMode = true; _channelModeHsv[key] = true; }
        GUILayout.EndHorizontal();

        GUILayout.Space(2f);
        if (hsvMode)
        {
            h = ChannelRow("Hue", h, key + ":h");
            s = ChannelRow("Saturation", s, key + ":s");
            v = ChannelRow("Value", v, key + ":v");
            rgb = Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Clamp01(s), Mathf.Clamp01(v));
            value = hdr
                ? new Color(rgb.r * intensity, rgb.g * intensity, rgb.b * intensity, value.a)
                : new Color(rgb.r, rgb.g, rgb.b, value.a);
        }
        else
        {
            // Post-intensity values for hdr (real magnitude, can exceed 1).
            // If the user actually typed a new number here (as opposed to
            // these just echoing the wheel/bar-derived value unchanged),
            // h/s/v/intensity must be re-derived from it right now — leaving
            // them stale would show the wheel marker at the OLD hue/sat next
            // frame even though the color itself already changed.
            var beforeR = value.r; var beforeG = value.g; var beforeB = value.b;
            value.r = ChannelRow("Red", value.r, key + ":r");
            value.g = ChannelRow("Green", value.g, key + ":g");
            value.b = ChannelRow("Blue", value.b, key + ":b");
            if (!Mathf.Approximately(beforeR, value.r) || !Mathf.Approximately(beforeG, value.g) || !Mathf.Approximately(beforeB, value.b))
                RederiveHsv(value, hdr, out h, out s, out v, out intensity);
        }

        if (showAlpha)
            value.a = ChannelRow("Alpha", value.a, key + ":a");

        GUILayout.Space(4f);
        var beforeHex = value;
        value = HexRow(value, key + ":hex");
        if (!ColorsApproxEqual(beforeHex, value))
            RederiveHsv(value, hdr, out h, out s, out v, out intensity);

        _hsvState[key] = (h, s, v, intensity);
        _lastColorValue[key] = value;
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            GUILayout.EndVertical();
        }
        return value;
    }

    private static void RederiveHsv(Color value, bool hdr, out float h, out float s, out float v, out float intensity)
    {
        intensity = hdr ? Mathf.Max(1f, Mathf.Max(value.r, Mathf.Max(value.g, value.b))) : 1f;
        var normalizedIn = hdr
            ? new Color(value.r / intensity, value.g / intensity, value.b / intensity, value.a)
            : value;
        Color.RGBToHSV(new Color(Mathf.Clamp01(normalizedIn.r), Mathf.Clamp01(normalizedIn.g), Mathf.Clamp01(normalizedIn.b)), out h, out s, out v);
    }

    private static readonly Dictionary<string, bool> _channelModeHsv = new();

    private static bool SegmentButton(string label, bool active)
    {
        var style = SafeStyle(GUI.skin != null ? GUI.skin.button : null, SC2Type.Body);
        style.normal.textColor = active ? SC2Theme.TextOnAccent : SC2Theme.TextSecondary;
        if (active && style.onNormal.background != null)
            style.normal.background = style.onNormal.background;
        return GUILayout.Button(label, style, GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.ExpandWidth(true));
    }

    /// <summary>One full-width "Red / 1.000"-style row — label left, live
    /// numeric box right, on its own tinted background bar (matches the
    /// reference picker's Red/Green/Blue/Alpha rows).
    ///
    /// Reserves the row as one explicit rect (same technique as Slider/
    /// IntSlider above) instead of BeginHorizontal + FlexibleSpace + an
    /// ExpandWidth text field — that combination is exactly the pattern
    /// already root-caused as unreliable once nested inside the panel's
    /// scroll view (confirmed via a real screenshot: the RGBA value boxes
    /// were getting clipped/hidden at the right edge).</summary>
    private static float ChannelRow(string label, float value, string key)
    {
        var rowStyle = new GUIStyle { normal = { background = ColoredTexture(SC2Theme.Accent) } };
        var row = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.ExpandWidth(true));
        GUI.Box(row, GUIContent.none, rowStyle);
        var pad = SC2UiScale.Pxf(8f);
        var boxWidth = SC2UiScale.Pxf(70f);
        var labelStyle = SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Body);
        labelStyle.normal.textColor = Color.white;
        var labelRect = new Rect(row.x + pad, row.y, row.width - boxWidth - pad * 2f, row.height);
        GUI.Label(labelRect, label, labelStyle);
        var boxRect = new Rect(row.xMax - pad - boxWidth, row.y + 3f, boxWidth, SC2UiScale.Pxf(18f));
        return RawFloatBoxAt(boxRect, value, key);
    }

    private static Color HexRow(Color value, string key)
    {
        var row = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.ExpandWidth(true));
        var labelStyle = SafeStyle(GUI.skin != null ? GUI.skin.label : null, SC2Type.Body);
        var labelWidth = SC2UiScale.Pxf(30f);
        var labelRect = new Rect(row.x, row.y, labelWidth, row.height);
        GUI.Label(labelRect, "Hex", labelStyle);
        var boxRect = new Rect(row.x + labelWidth + 4f, row.y, Mathf.Max(1f, row.width - labelWidth - 4f), row.height);

        var current = "#" + ColorUtility.ToHtmlStringRGBA(value);
        if (!_text.TryGetValue(key, out var text) || !_lastValueHex.TryGetValue(key, out var lastHex) || lastHex != current)
            text = current;
        GUI.SetNextControlName("SC2EnvNum_" + key);
        var next = GUI.TextField(boxRect, text, TextFieldStyle);
        _text[key] = next;
        TrackFocus("SC2EnvNum_" + key, boxRect);

        if (ColorUtility.TryParseHtmlString(next.StartsWith("#") ? next : "#" + next, out var parsed))
            value = parsed;
        _lastValueHex[key] = "#" + ColorUtility.ToHtmlStringRGBA(value);
        return value;
    }

    private static readonly Dictionary<string, string> _lastValueHex = new();

    private static readonly Dictionary<Color, Texture2D> _coloredTextures = new();
    private static Texture2D ColoredTexture(Color c)
    {
        if (_coloredTextures.TryGetValue(c, out var tex) && tex != null)
            return tex;

        tex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, c);
        tex.Apply(false, false);
        _coloredTextures[c] = tex;
        return tex;
    }

    private static bool DragInRect(Rect rect, out Vector2 t01)
    {
        t01 = Vector2.zero;
        var e = Event.current;
        if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && rect.Contains(e.mousePosition))
        {
            t01 = new Vector2(
                Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width),
                Mathf.Clamp01((e.mousePosition.y - rect.y) / rect.height));
            GUI.changed = true;
            e.Use();
            return true;
        }
        return false;
    }

    // ---- Circular hue/saturation wheel + vertical value bar ----
    // Matches the reference picker: angle = hue, distance from center =
    // saturation, full brightness (V=1) baked into the wheel itself; V is
    // controlled separately by the bar to the right (tinted by the current
    // hue/saturation, so a fully desaturated color shows a plain white->
    // black bar — exactly what a white swatch looks like in the reference).

    private static void DrawWheelMarker(Rect rect, float h, float s)
    {
        var cx = rect.x + rect.width * 0.5f;
        var cy = rect.y + rect.height * 0.5f;
        var maxRadius = Mathf.Min(rect.width, rect.height) * 0.5f - 2f;
        var angle = h * Mathf.PI * 2f;
        var mx = cx + Mathf.Cos(angle) * s * maxRadius;
        var my = cy + Mathf.Sin(angle) * s * maxRadius;
        const float sz = 8f;
        FillRect(new Rect(mx - sz * 0.5f - 1f, my - sz * 0.5f - 1f, sz + 2f, sz + 2f), Color.black);
        FillRect(new Rect(mx - sz * 0.5f, my - sz * 0.5f, sz, sz), Color.white);
        FillRect(new Rect(mx - sz * 0.5f + 1.5f, my - sz * 0.5f + 1.5f, sz - 3f, sz - 3f), Color.clear);
    }

    /// <summary>Mouse position -> (hue, saturation), using the exact same
    /// (dx, dyDisplay) convention GetWheelTexture bakes into the texture, so
    /// clicking a point on the wheel always yields the hue/saturation that
    /// point is actually painted with.</summary>
    private static bool DragInWheel(Rect rect, out float hue, out float sat)
    {
        hue = 0f; sat = 0f;
        var e = Event.current;
        if ((e.type != EventType.MouseDown && e.type != EventType.MouseDrag) || !rect.Contains(e.mousePosition))
            return false;

        var cx = rect.x + rect.width * 0.5f;
        var cy = rect.y + rect.height * 0.5f;
        var maxRadius = Mathf.Min(rect.width, rect.height) * 0.5f - 2f;
        var dx = e.mousePosition.x - cx;
        var dyDisplay = e.mousePosition.y - cy;
        var r = Mathf.Sqrt(dx * dx + dyDisplay * dyDisplay) / Mathf.Max(1f, maxRadius);
        sat = Mathf.Clamp01(r);
        var angle = Mathf.Atan2(dyDisplay, dx);
        hue = angle / (Mathf.PI * 2f);
        if (hue < 0f) hue += 1f;

        GUI.changed = true;
        e.Use();
        return true;
    }

    private static Texture2D _wheelTex;
    private static Texture2D GetWheelTexture()
    {
        if (_wheelTex != null)
            return _wheelTex;

        const int size = 96;
        const float center = (size - 1) * 0.5f;
        const float maxRadius = center - 1f;
        _wheelTex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (var ty = 0; ty < size; ty++)
        {
            // GUI.DrawTexture flips vertically (texture row 0 renders at the
            // bottom) — dyDisplay is the on-screen, y-down offset from
            // center that ends up at this texture row after that flip.
            var dyDisplay = center - ty;
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var r = Mathf.Sqrt(dx * dx + dyDisplay * dyDisplay) / maxRadius;
                if (r <= 1f)
                {
                    var angle = Mathf.Atan2(dyDisplay, dx);
                    var hue = angle / (Mathf.PI * 2f);
                    if (hue < 0f) hue += 1f;
                    var rgb = Color.HSVToRGB(hue, r, 1f);
                    var alpha = Mathf.Clamp01((1f - r) * size * 0.5f);
                    _wheelTex.SetPixel(x, ty, new Color(rgb.r, rgb.g, rgb.b, alpha));
                }
                else
                {
                    _wheelTex.SetPixel(x, ty, Color.clear);
                }
            }
        }
        _wheelTex.Apply(false, false);
        return _wheelTex;
    }

    private static void DrawValueBarMarker(Rect rect, float v)
    {
        var y = rect.y + (1f - Mathf.Clamp01(v)) * rect.height;
        FillRect(new Rect(rect.x - 2f, y - 2f, rect.width + 4f, 4f), Color.black);
        FillRect(new Rect(rect.x - 1f, y - 1f, rect.width + 2f, 2f), Color.white);
    }

    private static Texture2D _valueBarTex;
    private static float _valueBarHue = -1f, _valueBarSat = -1f;
    private static Texture2D GetValueBarTexture(float hue, float sat)
    {
        const int H = 64;
        if (_valueBarTex != null && Mathf.Approximately(_valueBarHue, hue) && Mathf.Approximately(_valueBarSat, sat))
            return _valueBarTex;

        if (_valueBarTex == null)
        {
            _valueBarTex = new Texture2D(1, H, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        // Same vertical-flip reasoning as the wheel: row (H-1) renders at
        // the top, so value=1 (bright) must live at row (H-1) for the
        // displayed top to read "full brightness", matching v = 1 - t.y.
        for (var y = 0; y < H; y++)
        {
            var v = (float)y / (H - 1);
            _valueBarTex.SetPixel(0, y, Color.HSVToRGB(hue, sat, v));
        }
        // makeNoLongerReadable MUST be false here (unlike a texture that's
        // only ever written once): this texture is regenerated in place
        // every time hue/sat changes, so sealing it non-readable after the
        // very first Apply() made every subsequent SetPixel() throw
        // "Texture '' is not readable" — the actual bug behind the crash
        // that fired the instant the wheel/value bar was dragged or a
        // channel was typed a second time.
        _valueBarTex.Apply(false, false);
        _valueBarHue = hue;
        _valueBarSat = sat;
        return _valueBarTex;
    }

    // ---- The picker in a floating panel (the runtime ColorField of the materials and pairs) ----

    /// <summary>What a floating colour picker remembers between frames: the colour as hue, saturation and value (kept apart, so
    /// dragging to black or white does not lose the hue), the alpha it started with, and the text of the hex field.</summary>
    internal sealed class PickerState
    {
        public float H, S, V, A = 1f;
        public string HexText = "";

        public Color Color
        {
            get
            {
                var rgb = Color.HSVToRGB(Mathf.Repeat(H, 1f), Mathf.Clamp01(S), Mathf.Clamp01(V));
                return new Color(rgb.r, rgb.g, rgb.b, A);
            }
        }

        public void Set(Color color)
        {
            A = color.a;
            Color.RGBToHSV(new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b)), out var h, out var s, out var v);
            // A grey has no hue of its own; keep the one the wheel was showing.
            if (s > 0.0001f && v > 0.0001f) H = h;
            S = s;
            V = v;
        }
    }

    private const int PickerPresetColumns = 10;

    /// <summary>Width and height of <see cref="DrawPickerBody"/> for a list of <paramref name="presetCount"/> preset swatches.</summary>
    internal static Vector2 PickerSize(int presetCount)
    {
        var pad = SC2UiScale.Pxf(8f);
        var wheel = SC2UiScale.Pxf(124f);
        var width = pad * 3f + wheel + SC2UiScale.Pxf(20f);
        var rows = Mathf.CeilToInt(presetCount / (float)PickerPresetColumns);
        var swatch = (width - pad * 2f) / PickerPresetColumns;
        var height = pad + wheel + pad                       // wheel and value bar
                     + SC2UiScale.Pxf(24f) + pad * 0.5f      // preview and hex
                     + 3f * SC2UiScale.Pxf(22f) + pad * 0.5f // R G B
                     + rows * swatch + pad;
        return new Vector2(width, height);
    }

    /// <summary>The whole picker inside <paramref name="area"/>: the hue and saturation wheel with its value bar, the colour as a
    /// hex field and as red, green and blue sliders, and a row of preset swatches. Returns true when the colour changed (by a
    /// drag, a typed value or a preset); read it from <paramref name="state"/>.</summary>
    internal static bool DrawPickerBody(Rect area, PickerState state, Color[] presets)
    {
        var changed = false;
        var e = Event.current;
        var pad = SC2UiScale.Pxf(8f);
        var wheelSize = SC2UiScale.Pxf(124f);

        var wheelRect = new Rect(area.x + pad, area.y + pad, wheelSize, wheelSize);
        GUI.DrawTexture(wheelRect, GetWheelTexture());
        DrawWheelMarker(wheelRect, state.H, state.S);
        if (DragInWheel(wheelRect, out var hue, out var saturation)) { state.H = hue; state.S = saturation; changed = true; }

        var barRect = new Rect(wheelRect.xMax + pad, wheelRect.y, SC2UiScale.Pxf(20f), wheelSize);
        GUI.DrawTexture(barRect, GetValueBarTexture(state.H, state.S));
        DrawBorder(barRect, BorderColor);
        DrawValueBarMarker(barRect, state.V);
        if (DragInRect(barRect, out var t)) { state.V = 1f - t.y; changed = true; }

        // preview and hex
        var y = wheelRect.yMax + pad;
        var rowHeight = SC2UiScale.Pxf(24f);
        var preview = new Rect(area.x + pad, y, SC2UiScale.Pxf(30f), rowHeight);
        var current = state.Color;
        FillRect(preview, new Color(current.r, current.g, current.b, 1f));
        DrawBorder(preview, BorderColor);
        var hexRect = new Rect(preview.xMax + pad, y, area.xMax - pad - preview.xMax - pad, rowHeight);
        const string hexControl = "SC2PickerHex";
        GUI.SetNextControlName(hexControl);
        var focused = GUI.GetNameOfFocusedControl() == hexControl;
        var typed = GUI.TextField(hexRect, focused ? state.HexText : "#" + ColorUtility.ToHtmlStringRGB(current), 9);
        if (focused)
        {
            state.HexText = typed;
            var digits = typed.StartsWith("#") ? typed.Substring(1) : typed;
            if ((digits.Length == 6 || digits.Length == 8) && ColorUtility.TryParseHtmlString("#" + digits, out var parsed)
                && (Mathf.Abs(parsed.r - current.r) > 0.002f || Mathf.Abs(parsed.g - current.g) > 0.002f || Mathf.Abs(parsed.b - current.b) > 0.002f))
            {
                parsed.a = state.A;
                state.Set(parsed);
                changed = true;
            }
        }
        else
        {
            state.HexText = typed;
        }

        // red, green, blue
        y += rowHeight + pad * 0.5f;
        var sliderHeight = SC2UiScale.Pxf(22f);
        var labelWidth = SC2UiScale.Pxf(16f);
        var numberWidth = SC2UiScale.Pxf(34f);
        var channels = new[] { current.r, current.g, current.b };
        var names = new[] { "R", "G", "B" };
        for (var i = 0; i < 3; i++)
        {
            var row = new Rect(area.x + pad, y + i * sliderHeight, area.width - pad * 2f, sliderHeight);
            GUI.Label(new Rect(row.x, row.y, labelWidth, row.height), names[i]);
            var slider = new Rect(row.x + labelWidth, row.y, row.width - labelWidth - numberWidth, row.height);
            var value = GUI.HorizontalSlider(slider, channels[i] * 255f, 0f, 255f);
            GUI.Label(new Rect(slider.xMax + pad * 0.5f, row.y, numberWidth, row.height), Mathf.RoundToInt(value).ToString());
            if (Mathf.Abs(value - channels[i] * 255f) > 0.5f)
            {
                channels[i] = Mathf.Round(value) / 255f;
                state.Set(new Color(channels[0], channels[1], channels[2], state.A));
                changed = true;
                current = state.Color;
            }
        }
        y += 3f * sliderHeight + pad * 0.5f;

        // presets
        if (presets != null)
        {
            var swatch = (area.width - pad * 2f) / PickerPresetColumns;
            for (var i = 0; i < presets.Length; i++)
            {
                var cell = new Rect(area.x + pad + (i % PickerPresetColumns) * swatch, y + (i / PickerPresetColumns) * swatch, swatch - 2f, swatch - 2f);
                FillRect(cell, presets[i]);
                if (e != null && e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
                {
                    var picked = presets[i];
                    picked.a = state.A;
                    state.Set(picked);
                    state.HexText = "#" + ColorUtility.ToHtmlStringRGB(picked);
                    GUI.FocusControl(null);
                    changed = true;
                    e.Use();
                }
            }
        }
        return changed;
    }
}
