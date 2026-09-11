using System.Numerics;
using Hexa.NET.ImGui;

namespace SC2InfoBdfEditorApp;

/// <summary>
/// Dark color palette, ported 1:1 (same RGB float values, 0..1 like
/// UnityEngine.Color) from Scripts/Unified/SC2UnifiedSkin.cs — the palette
/// every Unity-side unified-shell panel (including the embedded
/// SC2InfoBdfEditorPanel) is already painted with, itself designed to
/// resemble Unity Editor's own dark skin. Using the exact same numbers
/// here (not an independently eyeballed "similar" dark theme) means this
/// app and the in-game panel share one real visual identity.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 Bg = new(0.106f, 0.114f, 0.129f, 1f);
    public static readonly Vector4 Surface = new(0.208f, 0.220f, 0.251f, 1f);
    public static readonly Vector4 SurfaceHi = new(0.267f, 0.281f, 0.318f, 1f);
    public static readonly Vector4 Accent = new(0.357f, 0.576f, 0.929f, 1f);
    public static readonly Vector4 AccentDim = new(0.294f, 0.475f, 0.766f, 1f);
    public static readonly Vector4 Border = new(0.34f, 0.365f, 0.415f, 1f);
    public static readonly Vector4 TextPrimary = new(0.90f, 0.92f, 0.95f, 1f);
    public static readonly Vector4 TextSecondary = new(0.72f, 0.75f, 0.80f, 1f);
    public static readonly Vector4 FieldBg = new(0.09f, 0.096f, 0.11f, 1f);

    /// <summary>Applies the palette to every ImGui style color slot this
    /// app's widgets actually touch (sliders, checkboxes, buttons,
    /// collapsing headers, frames) — called once after ImGui.CreateContext,
    /// same as SC2EnvFieldGui's hand-drawn controls apply their own colors
    /// per-control rather than relying on Unity's default GUISkin.</summary>
    public static void Apply(ImGuiStylePtr style)
    {
        ImGui.StyleColorsDark(style);

        var c = style.Colors;
        c[(int)ImGuiCol.Text] = TextPrimary;
        c[(int)ImGuiCol.TextDisabled] = TextSecondary;
        c[(int)ImGuiCol.WindowBg] = Bg;
        c[(int)ImGuiCol.ChildBg] = Bg;
        c[(int)ImGuiCol.PopupBg] = Surface;
        c[(int)ImGuiCol.Border] = Border;
        c[(int)ImGuiCol.BorderShadow] = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = FieldBg;
        c[(int)ImGuiCol.FrameBgHovered] = SurfaceHi;
        c[(int)ImGuiCol.FrameBgActive] = AccentDim;
        c[(int)ImGuiCol.TitleBg] = Surface;
        c[(int)ImGuiCol.TitleBgActive] = Surface;
        c[(int)ImGuiCol.TitleBgCollapsed] = Surface;
        c[(int)ImGuiCol.MenuBarBg] = Surface;
        c[(int)ImGuiCol.ScrollbarBg] = Bg;
        c[(int)ImGuiCol.ScrollbarGrab] = SurfaceHi;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = AccentDim;
        c[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
        c[(int)ImGuiCol.CheckMark] = Accent;
        c[(int)ImGuiCol.SliderGrab] = Accent;
        c[(int)ImGuiCol.SliderGrabActive] = AccentDim;
        c[(int)ImGuiCol.Button] = Surface;
        c[(int)ImGuiCol.ButtonHovered] = SurfaceHi;
        c[(int)ImGuiCol.ButtonActive] = AccentDim;
        c[(int)ImGuiCol.Header] = Surface;
        c[(int)ImGuiCol.HeaderHovered] = SurfaceHi;
        c[(int)ImGuiCol.HeaderActive] = AccentDim;
        c[(int)ImGuiCol.Separator] = Border;
        c[(int)ImGuiCol.SeparatorHovered] = Accent;
        c[(int)ImGuiCol.SeparatorActive] = Accent;
        c[(int)ImGuiCol.ResizeGrip] = SurfaceHi;
        c[(int)ImGuiCol.ResizeGripHovered] = Accent;
        c[(int)ImGuiCol.ResizeGripActive] = Accent;
        c[(int)ImGuiCol.Tab] = Surface;
        c[(int)ImGuiCol.TabHovered] = SurfaceHi;
        c[(int)ImGuiCol.TabSelected] = AccentDim;
        c[(int)ImGuiCol.TabDimmed] = Surface;
        c[(int)ImGuiCol.TabDimmedSelected] = AccentDim;

        style.WindowRounding = 0f;
        style.FrameRounding = 2f;
        style.GrabRounding = 2f;
        style.FrameBorderSize = 1f;
        style.WindowBorderSize = 0f;
        // Roomier than ImGui's own default (8,4) — with ~90 fields stacked
        // in a single scroll region, extra vertical breathing room between
        // rows matters more here than in a typical dense tool window.
        style.ItemSpacing = new Vector2(8f, 10f);
        style.FramePadding = new Vector2(6f, 5f);
    }
}
