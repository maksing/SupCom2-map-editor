#if !UNITY_EDITOR
using System;
using UnityEngine;
#if UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Standalone (Build &amp; Run) player window behavior — minimize / maximize /
/// close are handled EXCLUSIVELY by the native OS title bar, no custom in-UI
/// buttons for any of them.
///
/// The native title bar's maximize box only lights up if the window actually
/// has the WS_MAXIMIZEBOX/WS_THICKFRAME styles. Whether Unity creates the
/// window with those depends on Player Settings → Resolution and
/// Presentation → Resizable Window, baked in at build time — and on this
/// project's (pre-2021.2) Unity version there's no Screen.resizableWindow to
/// flip that at runtime. So instead of adding our own maximize button, this
/// patches the REAL OS window's style bits directly via user32
/// (GetWindowLong/SetWindowLong + SetWindowPos/SWP_FRAMECHANGED) on startup —
/// the native title bar itself becomes resizable/maximizable, no matter what
/// the build's Player Settings said. Same technique engines used before
/// Unity exposed Screen.resizableWindow.
/// </summary>
public static class SC2RuntimeWindow
{
#if UNITY_STANDALONE_WIN
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_THICKFRAME  = 0x00040000L; // resizable border — required for the OS to allow maximize/resize at all

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar to go with the dark interface: the dark-mode flag (Windows 10 2004 and later) and, where Windows 11 allows
    /// it, the caption, text and border colours of the theme. Anything the system does not know is quietly ignored.</summary>
    private static void ApplyDarkTitleBar(IntPtr hWnd)
    {
        try
        {
            var dark = 1;
            if (DwmSetWindowAttribute(hWnd, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hWnd, 19, ref dark, sizeof(int));
            var caption = ColorRef(SC2Theme.Bg1);
            DwmSetWindowAttribute(hWnd, 35, ref caption, sizeof(int));
            var text = ColorRef(SC2Theme.TextPrimary);
            DwmSetWindowAttribute(hWnd, 36, ref text, sizeof(int));
            var border = ColorRef(SC2Theme.Line);
            DwmSetWindowAttribute(hWnd, 34, ref border, sizeof(int));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeWindow] could not set the dark title bar: {ex.Message}");
        }
    }

    private static void RefreshTitleBarColors()
    {
        var hWnd = ResolveWindowHandle();
        if (hWnd != IntPtr.Zero) ApplyDarkTitleBar(hWnd);
    }

    /// <summary>A Windows COLORREF (0x00BBGGRR).</summary>
    private static int ColorRef(Color color)
    {
        var c = (Color32)color;
        return c.r | (c.g << 8) | (c.b << 16);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static long GetWindowStyle(IntPtr hWnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64() : GetWindowLong32(hWnd, GWL_STYLE);

    private static void SetWindowStyle(IntPtr hWnd, long style)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hWnd, GWL_STYLE, new IntPtr(style));
        else
            SetWindowLong32(hWnd, GWL_STYLE, unchecked((int)style));
    }

    /// <summary>Internal (not private) so SC2RuntimeQuitConfirm can resolve
    /// the same, more-reliable handle (Process.MainWindowHandle first,
    /// GetActiveWindow as fallback) instead of its own bare GetActiveWindow
    /// call — see that class's own comment on why a wrong/zero owner handle
    /// matters for MessageBoxW's modal behavior.</summary>
    internal static IntPtr ResolveWindowHandle()
    {
        try
        {
            var handle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            return handle != IntPtr.Zero ? handle : GetActiveWindow();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeWindow] could not resolve window handle: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static bool _nativeMaximizeApplied;

    /// <summary>
    /// Adds the window styles the native title bar needs to enable its own
    /// maximize box and let the OS resize/maximize the window, then tells
    /// Windows to redraw the (cached) title bar frame so the change actually
    /// shows up — SetWindowPos with SWP_FRAMECHANGED and no actual move/resize.
    ///
    /// Returns true once applied. A single attempt at OnEnable was unreliable
    /// (the "maximize button inactive again" regression): Process.
    /// MainWindowHandle/GetActiveWindow can both still return IntPtr.Zero on
    /// the very first frame(s) after the native window is created — before it
    /// has a title or has been given focus yet — so a one-shot call can
    /// silently no-op depending on exact startup timing. Callers should keep
    /// calling this (e.g. once per frame from Update) until it returns true.
    /// </summary>
    private static bool TryEnableNativeResizeAndMaximize()
    {
        if (_nativeMaximizeApplied)
            return true;

        var hWnd = ResolveWindowHandle();
        if (hWnd == IntPtr.Zero)
            return false; // window not ready yet — retry next frame

        try
        {
            var style = GetWindowStyle(hWnd);
            style |= WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_THICKFRAME;
            SetWindowStyle(hWnd, style);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            ApplyDarkTitleBar(hWnd);
            // The caption colours come from the palette, so they follow it when the user changes a colour.
            SC2Theme.Applied += RefreshTitleBarColors;
            _nativeMaximizeApplied = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeWindow] could not enable native maximize: {ex.Message}");
            return false;
        }
    }
#endif

    /// <summary>
    /// Call once per frame (e.g. from Update) until it returns true — retries
    /// the native maximize-box style patch if the previous attempt(s) couldn't
    /// resolve a window handle yet. No-op once already applied, and a no-op
    /// entirely outside Windows standalone.
    /// </summary>
    public static bool RetryEnableNativeMaximizeIfNeeded()
    {
#if UNITY_STANDALONE_WIN
        return TryEnableNativeResizeAndMaximize();
#else
        return true;
#endif
    }

    private static int _windowedWidth;
    private static int _windowedHeight;

    public static void EnsureWindowedOnStartup()
    {
        _windowedWidth = Mathf.Max(1024, Screen.width);
        _windowedHeight = Mathf.Max(640, Screen.height);

        if (Screen.width < 1024 || Screen.height < 640)
        {
            _windowedWidth = Mathf.Min(1600, Mathf.Max(1024, Screen.currentResolution.width - 200));
            _windowedHeight = Mathf.Min(900, Mathf.Max(640, Screen.currentResolution.height - 200));
        }

        Screen.SetResolution(_windowedWidth, _windowedHeight, FullScreenMode.Windowed);
        TrySetResizableWindow();

        // Best-effort immediate attempt; RetryEnableNativeMaximizeIfNeeded()
        // (called every frame from SC2GameViewEditorUI.Update) picks it up if
        // the window handle isn't resolvable yet on this very first call.
        RetryEnableNativeMaximizeIfNeeded();
    }

    public static bool IsFullscreen => Screen.fullScreenMode != FullScreenMode.Windowed;

    /// <summary>
    /// True exclusive/borderless fullscreen — not achievable via any native OS
    /// window control, so it needs its own toggle (wired to F11).
    ///
    /// Explicitly resizes to the MONITOR's native resolution on entry: just
    /// flipping fullScreenMode without also setting width/height keeps
    /// whatever the current windowed size was and the OS stretches that
    /// framebuffer to fill the actual display — a GPU upscale that reads as
    /// blurry/low-quality UI. Returning to windowed restores the exact
    /// previous windowed size.
    /// </summary>
    public static void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            Screen.SetResolution(_windowedWidth, _windowedHeight, FullScreenMode.Windowed);
        }
        else
        {
            _windowedWidth = Screen.width;
            _windowedHeight = Screen.height;
            var res = Screen.currentResolution;
            Screen.SetResolution(res.width, res.height, FullScreenMode.FullScreenWindow);
        }
    }

    /// <summary>
    /// Sets UnityEngine.Screen.resizableWindow = true via reflection instead of
    /// a direct call: that property only exists on Unity 2021.2+, and this
    /// project's Unity version doesn't have it (direct usage is a compile
    /// error — CS0117 — there). Reflection compiles on any version and is a
    /// silent no-op where the property doesn't exist; the Win32 style patch
    /// above is what actually makes maximize work on this project's version.
    /// </summary>
    private static void TrySetResizableWindow()
    {
        try
        {
            var property = typeof(Screen).GetProperty("resizableWindow", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            property?.SetValue(null, true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeWindow] could not set Screen.resizableWindow: {ex.Message}");
        }
    }
}
#endif
