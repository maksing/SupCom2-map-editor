using UnityEngine;

/// <summary>
/// The hand-drawn widgets of the editor's look: a slider whose track fills with the accent up to the thumb, an on/off switch, a
/// section heading with an accent tick, and the cells of a segmented control. Shared by every panel so they all draw the same way.
/// </summary>
public static class SC2ForgeUi
{
    private static Texture2D _trackTex, _fillTex, _switchOffTex, _switchOnTex, _knobTex, _segNormalTex, _segHoverTex, _segSelectedTex, _segContainerTex, _chipTex;
    private static GUIStyle _trackStyle, _fillStyle, _clearTrackStyle, _sectionStyle, _hintStyle, _segStyle, _segSelectedStyle, _segContainerStyle, _chipStyle, _chipTextStyle;
    private static Texture2D _whitePixel;
    private static float _builtForScale = -1f;
    private static int _builtThemeVersion = -1;

    private static void EnsureStyles()
    {
        if (_trackStyle != null && _trackTex != null && Mathf.Approximately(_builtForScale, SC2UiScale.Scale) && _builtThemeVersion == SC2Theme.RenderVersion) return;
        _builtForScale = SC2UiScale.Scale;
        _builtThemeVersion = SC2Theme.RenderVersion;

        _trackTex = SC2StartupUi.Rounded(SC2Theme.SliderTrack, SC2Theme.SliderTrack, 3, 0f);
        _fillTex = SC2StartupUi.Rounded(SC2Theme.Accent, SC2Theme.Accent, 3, 0f);
        _switchOffTex = SC2StartupUi.Rounded(SC2Theme.FieldFill, SC2Theme.LineStrong, 8, 1f);
        _switchOnTex = SC2StartupUi.Rounded(SC2Theme.Accent, SC2Theme.Accent, 8, 0f);
        _knobTex = SC2StartupUi.Rounded(Color.white, Color.white, 6, 0f);
        _segNormalTex = SC2StartupUi.Rounded(SC2Theme.Bg1, SC2Theme.LineStrong, 5, 1f);
        _segHoverTex = SC2StartupUi.Rounded(SC2Theme.Bg4, SC2Theme.Bg4, 5, 0f);
        _segContainerTex = SC2StartupUi.Rounded(SC2Theme.Bg0, SC2Theme.Line, 6, 1f);
        _chipTex = SC2StartupUi.Rounded(SC2Theme.Bg3, SC2Theme.LineStrong, 4, 1f);
        _segSelectedTex = SC2StartupUi.Rounded(SC2Theme.SelectedFillOver(SC2Theme.Bg1), SC2Theme.SelectedBorderOver(SC2Theme.Bg1), 5, 1.25f);

        _trackStyle = new GUIStyle { border = new RectOffset(4, 4, 4, 4) };
        _trackStyle.normal.background = _trackTex;
        _fillStyle = new GUIStyle { border = new RectOffset(4, 4, 4, 4) };
        _fillStyle.normal.background = _fillTex;

        // The slider control itself draws only its thumb; the track and the fill are drawn here, under it.
        _clearTrackStyle = new GUIStyle();

        _sectionStyle = new GUIStyle { fontSize = SC2Type.Caption, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _sectionStyle.normal.textColor = SC2Theme.TextSecondary;

        _hintStyle = new GUIStyle { fontSize = SC2Type.Caption, alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip };
        _hintStyle.normal.textColor = SC2Theme.TextDisabled;

        _segContainerStyle = new GUIStyle { border = new RectOffset(7, 7, 7, 7) };
        _segContainerStyle.normal.background = _segContainerTex;

        _chipStyle = new GUIStyle
        {
            fontSize = SC2Type.Micro, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip,
            border = new RectOffset(5, 5, 5, 5),
            padding = new RectOffset(SC2UiScale.Px(5), SC2UiScale.Px(5), 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        _chipStyle.normal.background = _chipTex;
        _chipStyle.normal.textColor = SC2Theme.TextBright;
        _chipTextStyle = new GUIStyle { fontSize = SC2Type.Caption, alignment = TextAnchor.MiddleLeft, margin = new RectOffset(0, 0, 0, 0) };
        _chipTextStyle.normal.textColor = SC2Theme.TextSecondary;

        _segStyle = new GUIStyle { fontSize = SC2Type.Body, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip, border = new RectOffset(6, 6, 6, 6) };
        _segStyle.normal.background = null;
        _segStyle.hover.background = _segHoverTex;
        foreach (var state in new[] { _segStyle.normal, _segStyle.hover }) state.textColor = SC2Theme.TextBright;
        _segSelectedStyle = new GUIStyle(_segStyle) { fontStyle = FontStyle.Bold };
        _segSelectedStyle.normal.background = _segSelectedTex;
        _segSelectedStyle.hover.background = _segSelectedTex;
        foreach (var state in new[] { _segSelectedStyle.normal, _segSelectedStyle.hover }) state.textColor = SC2Theme.AccentText;
    }

    private static Texture2D White
    {
        get
        {
            if (_whitePixel == null)
            {
                _whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _whitePixel.SetPixel(0, 0, Color.white);
                _whitePixel.Apply(false, false);
            }
            return _whitePixel;
        }
    }

    /// <summary>A horizontal slider in the given rect: a slim track that is filled with the accent from the left up to the thumb.</summary>
    public static float Slider(Rect rect, float value, float min, float max)
    {
        EnsureStyles();
        var thumb = GUI.skin.horizontalSliderThumb;
        var thumbWidth = thumb != null && thumb.fixedWidth > 0f ? thumb.fixedWidth : rect.height;
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            var trackHeight = Mathf.Max(3f, SC2UiScale.Pxf(4f));
            var track = new Rect(rect.x, rect.center.y - trackHeight * 0.5f, rect.width, trackHeight);
            _trackStyle.Draw(track, GUIContent.none, false, false, false, false);
            var t = Mathf.Approximately(max, min) ? 0f : Mathf.Clamp01((value - min) / (max - min));
            var fillWidth = Mathf.Clamp(thumbWidth * 0.5f + t * (rect.width - thumbWidth), trackHeight, rect.width);
            _fillStyle.Draw(new Rect(track.x, track.y, fillWidth, trackHeight), GUIContent.none, false, false, false, false);
        }
        return GUI.HorizontalSlider(rect, value, min, max, _clearTrackStyle, thumb);
    }

    /// <summary>A small on/off switch drawn in the given rect (width about twice the height). Returns the new state; the caller owns the click.</summary>
    public static void DrawSwitch(Rect rect, bool on, bool hover)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        EnsureStyles();
        var previous = GUI.color;
        GUI.color = hover && !on ? new Color(1.25f, 1.25f, 1.25f, 1f) : Color.white;
        GUI.DrawTexture(rect, on ? _switchOnTex : _switchOffTex, ScaleMode.StretchToFill, true);
        var knob = rect.height - 6f;
        var knobX = on ? rect.xMax - knob - 3f : rect.x + 3f;
        GUI.color = on ? Color.white : SC2Theme.TextSecondary;
        GUI.DrawTexture(new Rect(knobX, rect.y + 3f, knob, knob), _knobTex);
        GUI.color = previous;
    }

    /// <summary>A heading for a block of controls: a small accent tick, then the title in capitals in the hint colour, and optionally a
    /// small hint (a key, say) at the right edge.</summary>
    public static void Section(string text, string hint = null)
    {
        EnsureStyles();
        var rect = GUILayoutUtility.GetRect(0f, SC2UiScale.Pxf(14.5f), GUILayout.ExpandWidth(true));
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        var previous = GUI.color;
        GUI.color = SC2Theme.Accent;
        GUI.DrawTexture(new Rect(rect.x, rect.y + 2f, Mathf.Max(2f, SC2UiScale.Pxf(2.4f)), rect.height - 4f), White);
        GUI.color = previous;
        GUI.Label(new Rect(rect.x + SC2UiScale.Pxf(10f), rect.y, rect.width - SC2UiScale.Pxf(10f), rect.height), (text ?? "").ToUpperInvariant(), _sectionStyle);
        if (!string.IsNullOrEmpty(hint))
            GUI.Label(rect, hint, _hintStyle);
    }

    /// <summary>The border and dark fill that hold one row of a segmented control.</summary>
    public static void SegmentContainer(Rect rect)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        EnsureStyles();
        _segContainerStyle.Draw(rect, GUIContent.none, false, false, false, false);
    }

    /// <summary>A keyboard key drawn as a small chip (F, Ctrl, Scroll), with the label that follows it.</summary>
    public static void KeyChip(string key)
    {
        EnsureStyles();
        GUILayout.Label(key, _chipStyle, GUILayout.Height(SC2UiScale.Pxf(15f)));
    }

    public static void ChipText(string text)
    {
        EnsureStyles();
        GUILayout.Label(text, _chipTextStyle, GUILayout.Height(SC2UiScale.Pxf(15f)));
    }

    /// <summary>One cell of a segmented control: rounded, with the selected one outlined in the accent.</summary>
    public static void SegmentCell(Rect rect, string label, bool selected, bool hover)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        EnsureStyles();
        (selected ? _segSelectedStyle : _segStyle).Draw(rect, label, hover, false, false, false);
    }
}
