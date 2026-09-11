#if !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Native "Save changes before closing?" confirmation for standalone
/// (Build &amp; Run) builds — EditorUtility.DisplayDialogComplex does not exist
/// outside the Editor. Calls straight into the real Win32 MessageBoxW (a
/// real three-button Yes/No/Cancel dialog) rather than building a custom
/// GUILayout modal overlay — the same convention SC2RuntimeFileDialog
/// already established for this project's Build-only native dialogs.
/// </summary>
public static class SC2RuntimeQuitConfirm
{
    public enum Result { Save, DontSave, Cancel }

#if UNITY_STANDALONE_WIN
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    private const uint MB_YESNOCANCEL = 0x00000003;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SYSTEMMODAL = 0x00001000;
    private const int IDYES = 6;
    private const int IDNO = 7;
#endif

    /// <summary>Blocks (a real modal Win32 call, same as SC2RuntimeFileDialog's
    /// own OpenFile/SaveFile) until the user answers. On any platform where
    /// the native dialog isn't available (Windows-only today, same scope as
    /// SC2RuntimeFileDialog), returns Save — the safest default when a real
    /// prompt can't be shown is to save, not to silently discard work.</summary>
    public static Result Ask(string mapName)
    {
#if UNITY_STANDALONE_WIN
        var text = string.IsNullOrEmpty(mapName)
            ? "Save changes to the current map before closing?"
            : $"Save changes to '{mapName}' before closing?";
        // GetActiveWindow() can resolve to a handle that isn't the real main
        // window (or IntPtr.Zero) depending on focus/timing, so MessageBoxW's
        // automatic "disable the owner while shown" never actually kicked in
        // — the main window stayed clickable, letting further close clicks
        // re-enter the WM_CLOSE/wantsToQuit chain while this call was still
        // blocking, which can leave Unity's own quit bookkeeping confused
        // even though the ORIGINAL dialog eventually resolves Save/DontSave
        // correctly, and the dialog buttons appear not to actually exit.
        // Resolve the
        // same, more reliable handle SC2RuntimeWindow already uses
        // (Process.MainWindowHandle first) and explicitly disable it for the
        // duration of the call — belt-and-braces on top of MessageBoxW's own
        // automatic disable, since that one silently no-ops when the owner
        // handle is wrong.
        var owner = SC2RuntimeWindow.ResolveWindowHandle();
        if (owner != IntPtr.Zero) EnableWindow(owner, false);
        int response;
        try
        {
            response = MessageBoxW(owner, text, "SC2 Map Editor", MB_YESNOCANCEL | MB_ICONWARNING | MB_SYSTEMMODAL);
        }
        finally
        {
            if (owner != IntPtr.Zero) EnableWindow(owner, true);
        }
        if (response == IDYES) return Result.Save;
        if (response == IDNO) return Result.DontSave;
        return Result.Cancel;
#else
        Debug.LogWarning("[SC2RuntimeQuitConfirm] Native quit-confirm dialog only implemented for Windows standalone; saving by default.");
        return Result.Save;
#endif
    }
}
#endif
