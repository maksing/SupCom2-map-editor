using System;
using UnityEngine;

/// <summary>
/// Global UI scale multiplier for the whole unified GUI (fonts, buttons,
/// panel width, row heights) — a live scale parameter rather than a
/// one-time hardcoded size bump. Built as a live, persisted
/// parameter (PlayerPrefs, same pattern SC2UnitPropBrowser's GameDataRoot
/// already uses) rather than a one-time hardcoded bump, so it stays
/// adjustable from the View menu instead of being fixed forever at one
/// percentage — the same one-time editing pass either approach needs
/// (every literal font/button/panel size has to be touched once regardless)
/// buys a live parameter for free instead of a hardcoded constant.
///
/// Deliberately NOT a Unity GUI.matrix scale around the whole OnGUI (the
/// "free" alternative that needs zero per-literal edits) — a global matrix
/// scale does not auto-correct Event.current.mousePosition, and this
/// project's viewport raycasting (terrain paint, object placement/picking —
/// SC2TopViewMirror.ScreenPointToRay, ~112 mouse-position call sites across
/// SC2TerrainShell/SC2CostsShell/SC2ObjectsShell) assumes raw 1:1 screen
/// pixels; scaling the WHOLE OnGUI would silently break click-to-paint/
/// place/pick accuracy the moment Scale != 1. Instead, every affected
/// GUIStyle.fontSize and every hardcoded pixel Width/Height/RectOffset
/// literal reads through F/Px/Pxf below and requests a genuinely BIGGER
/// layout size — Unity's own GUIClip hit-testing handles that correctly
/// with zero special-casing, since nothing here is a coordinate-space
/// transform, just different numbers.
/// </summary>
public static class SC2UiScale
{
    private const string PrefKey = "SC2.UiScale";
    public const float Min = 1f;
    public const float Max = 2f;

    private static float _scale = -1f; // -1 = not loaded from PlayerPrefs yet

    public static float Scale
    {
        get
        {
            if (_scale < 0f) _scale = Mathf.Clamp(PlayerPrefs.GetFloat(PrefKey, 1f), Min, Max);
            return _scale;
        }
        set
        {
            var clamped = Mathf.Clamp(value, Min, Max);
            if (_scale >= 0f && Mathf.Approximately(clamped, _scale)) return;
            _scale = clamped;
            PlayerPrefs.SetFloat(PrefKey, _scale);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Fired whenever Scale actually changes — every cached style
    /// builder (SC2UnifiedSkin, SC2GameViewEditorUI.EnsureStyles) subscribes
    /// to invalidate its own cache and rebuild at the new scale, since
    /// neither rebuilds unconditionally every frame by design (baked
    /// textures/styles are real allocations, deliberately built once and
    /// reused — see each class's own "liveness sentinel" comment).</summary>
    public static event Action Changed;

    /// <summary>The size the interface is designed at, relative to the base numbers written in code: the reference layout is about a
    /// quarter larger than those numbers (14 px menu text where the code says 11), so every size goes through this factor too. The
    /// View menu's UI Scale then adjusts on top of it, 1.0 meaning the design size.</summary>
    public const float DesignFactor = 1.25f;

    /// <summary>The scale sizes are actually multiplied by: the user's UI Scale times the design factor.</summary>
    public static float Factor => Scale * DesignFactor;

    /// <summary>Scaled font size for a GUIStyle.fontSize assignment.</summary>
    public static int F(int baseFontSize) => Mathf.RoundToInt(baseFontSize * Factor);

    /// <summary>Scaled pixel size for a GUILayout.Width(int)/Height(int) call site.</summary>
    public static int Px(int basePixels) => Mathf.RoundToInt(basePixels * Factor);

    /// <summary>Scaled pixel size for a float-typed field (fixedHeight,
    /// fixedWidth, RectOffset components built from a float, etc.).</summary>
    public static float Pxf(float basePixels) => basePixels * Factor;

    // The start window keeps its own sizes (they are not part of the editor layout): the user's UI Scale only.
    public static int FRaw(int baseFontSize) => Mathf.RoundToInt(baseFontSize * Scale);
    public static int PxRaw(int basePixels) => Mathf.RoundToInt(basePixels * Scale);
    public static float PxfRaw(float basePixels) => basePixels * Scale;

    private static GUIStyle _popupStyle;
    private static float _popupStyleBuiltAtScale = -1f;

    /// <summary>Cached, scale-aware style for Popup dropdowns — both the real
    /// UnityEditor.EditorGUILayout.Popup used in the Editor (SC2MaterialsPairsMode's
    /// Material Preset/State pickers, SC2PaintMode's pair pickers) and this
    /// project's own SC2RuntimeDropdown-backed shim used in a Build. Neither
    /// picks up SC2UiScale from its own default styling on its own (native
    /// EditorStyles.popup ignores it entirely; the shim's default GUI.skin.button
    /// is Unity's unscaled built-in skin, not SC2UnifiedSkin), so every Popup
    /// call site passes this explicitly instead.</summary>
    public static GUIStyle PopupStyle()
    {
        if (_popupStyle == null || !Mathf.Approximately(_popupStyleBuiltAtScale, Scale))
        {
            GUIStyle basis = null;
#if UNITY_EDITOR
            try { basis = UnityEditor.EditorStyles.popup; } catch { /* fall through */ }
#endif
            if (basis == null)
            {
                try { basis = GUI.skin != null ? GUI.skin.button : null; } catch { /* fall through */ }
            }
            try { _popupStyle = basis != null ? new GUIStyle(basis) : new GUIStyle(); }
            catch (System.NullReferenceException) { _popupStyle = new GUIStyle(); }
            _popupStyle.fontSize = SC2Type.Body;
            _popupStyleBuiltAtScale = Scale;
        }
        return _popupStyle;
    }
}
