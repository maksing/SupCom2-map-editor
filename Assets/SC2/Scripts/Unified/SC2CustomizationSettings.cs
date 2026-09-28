using UnityEngine;

/// <summary>App-wide appearance preferences that are NOT part of the map
/// document — PlayerPrefs-backed, same pattern SC2SaveSettings already uses
/// for Save/Autosave/Backup (see that class' own doc comment for why
/// PlayerPrefs + primitives is "the" settings mechanism here). Picking a
/// decal marker color is a per-user editor preference, not authored map
/// data, so it lives here instead of in SC2DecalsDocument — it follows the
/// mapmaker across every map they open, the same map opened by someone else
/// still shows their own chosen color, and it never shows up as a diff in
/// the saved terrain.</summary>
public static class SC2CustomizationSettings
{
    private const string DecalMarkerColorRPrefKey = "SC2.Customization.DecalMarkerColor.R";
    private const string DecalMarkerColorGPrefKey = "SC2.Customization.DecalMarkerColor.G";
    private const string DecalMarkerColorBPrefKey = "SC2.Customization.DecalMarkerColor.B";

    /// <summary>The cyan SC2DecalsShell.DrawOriginMarker originally hardcoded
    /// for an unselected pin's fill.</summary>
    public static readonly Color DefaultDecalMarkerColor = new(0.15f, 0.9f, 1f);

    // null = "not loaded from PlayerPrefs yet" — same lazy-static-field
    // pattern SC2SaveSettings' own fields already use.
    private static Color? _decalMarkerColor;

    public static Color DecalMarkerColor
    {
        get
        {
            _decalMarkerColor ??= new Color(
                PlayerPrefs.GetFloat(DecalMarkerColorRPrefKey, DefaultDecalMarkerColor.r),
                PlayerPrefs.GetFloat(DecalMarkerColorGPrefKey, DefaultDecalMarkerColor.g),
                PlayerPrefs.GetFloat(DecalMarkerColorBPrefKey, DefaultDecalMarkerColor.b));
            return _decalMarkerColor.Value;
        }
        set
        {
            _decalMarkerColor = value;
            PlayerPrefs.SetFloat(DecalMarkerColorRPrefKey, value.r);
            PlayerPrefs.SetFloat(DecalMarkerColorGPrefKey, value.g);
            PlayerPrefs.SetFloat(DecalMarkerColorBPrefKey, value.b);
            PlayerPrefs.Save();
        }
    }
}
