#if !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Native "open file" dialog for standalone (Build &amp; Run) builds, where the
/// Editor's EditorUtility.OpenFilePanel does not exist.
///
/// On Windows it calls the OS common dialog (comdlg32 GetOpenFileNameW) so the
/// user gets the real system file picker. On any other platform it returns
/// null with a warning (the SC2 tooling targets Windows standalone; a
/// cross-platform in-GUI browser would be a separate feature).
/// </summary>
public static class SC2RuntimeFileDialog
{
#if UNITY_STANDALONE_WIN
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int structSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string filter;
        public string customFilter;
        public int maxCustFilter;
        public int filterIndex;
        public string file;
        public int maxFile;
        public string fileTitle;
        public int maxFileTitle;
        public string initialDir;
        public string title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        public string defExt;
        public IntPtr custData;
        public IntPtr hook;
        public string templateName;
        public IntPtr reservedPtr;
        public int reservedInt;
        public int flagsEx;
    }

    private const int OFN_FILEMUSTEXIST   = 0x00001000;
    private const int OFN_PATHMUSTEXIST   = 0x00000800;
    private const int OFN_NOCHANGEDIR     = 0x00000008;
    private const int OFN_EXPLORER        = 0x00080000;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    // Modern (Vista+) folder picker via IFileOpenDialog/FOS_PICKFOLDERS —
    // replaces the legacy SHBrowseForFolder tree dialog this used to call.
    // That older API takes a BROWSEINFO.pszDisplayName field the Win32 side
    // treats as a writable MAX_PATH output buffer, but .NET's default struct
    // marshaling for a `string` field only ever copies IN, never back out —
    // a well-known interop trap for this exact struct. In practice it didn't
    // corrupt the field we actually read (the returned PIDL), but it left
    // the dialog's own internal navigation state resting on whatever native
    // buffer .NET happened to marshal, and confirmed real: picking a folder
    // through it silently resolved to just the drive root ("D:\") instead of
    // the folder actually selected. IFileOpenDialog has no such field —
    // GetDisplayName below returns the full path via a fresh CoTaskMem
    // allocation every time — and is also the modern dialog Windows itself
    // shows elsewhere (Quick Access sidebar, address bar), matching what
    // EditorUtility.OpenFolderPanel already gives Editor users.
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    // Flat vtable covering IModalWindow.Show + every IFileDialog method (in
    // their real declared order) + IFileOpenDialog's own 2 extra methods —
    // COM dispatch only cares about vtable slot order, so there's no need
    // for separate IModalWindow/IFileDialog interface types just to express
    // the inheritance chain; only the methods this file actually calls
    // (Show/SetOptions/GetResult) have full signatures, the rest exist
    // purely to keep every later slot at its correct offset.
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_NOCHANGEDIR_FOD = 0x00000008;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);
#endif

    /// <summary>
    /// Shows a modal open-file dialog. <paramref name="filterLabel"/>/<paramref name="extension"/>
    /// build a single filter (e.g. "SC2 map (*.scd)", "scd"). Returns the picked
    /// absolute path, or null if the user cancelled / the platform is unsupported.
    /// </summary>
    public static string OpenFile(string title, string filterLabel, string extension)
    {
#if UNITY_STANDALONE_WIN
        var ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        // Filter string is a set of double-null-terminated "label\0pattern\0" pairs,
        // itself terminated by an extra null — C# builds it with explicit \0s.
        ofn.filter = $"{filterLabel}\0*.{extension}\0All files\0*.*\0\0";
        ofn.file = new string(new char[4096]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[512]);
        ofn.maxFileTitle = ofn.fileTitle.Length;
        ofn.title = title;
        ofn.defExt = extension;
        ofn.flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;

        try
        {
            ofn.hwndOwner = GetActiveWindow();
        }
        catch
        {
            ofn.hwndOwner = IntPtr.Zero;
        }

        try
        {
            if (GetOpenFileNameW(ref ofn))
                return ofn.file;
            return null; // user cancelled (or dialog error — treated the same)
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SC2RuntimeFileDialog] native open dialog failed: {ex.Message}");
            return null;
        }
#else
        Debug.LogWarning("[SC2RuntimeFileDialog] Native file dialog is only implemented for Windows standalone builds.");
        return null;
#endif
    }

    /// <summary>
    /// Shows a modal save-file dialog — the standalone-build equivalent of
    /// the Editor's EditorUtility.SaveFilePanel. Returns the picked absolute
    /// path (file need not already exist), or null if the user cancelled /
    /// the platform is unsupported.
    /// </summary>
    public static string SaveFile(string title, string filterLabel, string extension)
    {
#if UNITY_STANDALONE_WIN
        var ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        ofn.filter = $"{filterLabel}\0*.{extension}\0All files\0*.*\0\0";
        ofn.file = new string(new char[4096]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[512]);
        ofn.maxFileTitle = ofn.fileTitle.Length;
        ofn.title = title;
        ofn.defExt = extension;
        // No OFN_FILEMUSTEXIST here (unlike OpenFile) - the whole point of a
        // Save dialog is naming a file that may not exist yet.
        ofn.flags = OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_OVERWRITEPROMPT;

        try
        {
            ofn.hwndOwner = GetActiveWindow();
        }
        catch
        {
            ofn.hwndOwner = IntPtr.Zero;
        }

        try
        {
            if (GetSaveFileNameW(ref ofn))
                return ofn.file;
            return null; // user cancelled (or dialog error — treated the same)
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SC2RuntimeFileDialog] native save dialog failed: {ex.Message}");
            return null;
        }
#else
        Debug.LogWarning("[SC2RuntimeFileDialog] Native file dialog is only implemented for Windows standalone builds.");
        return null;
#endif
    }

    /// <summary>
    /// Shows a modal "browse for folder" dialog — the standalone-build
    /// equivalent of the Editor's EditorUtility.OpenFolderPanel (which does
    /// nothing outside the Editor). Returns the picked absolute folder path,
    /// or null if the user cancelled / the platform is unsupported.
    /// </summary>
    public static string OpenFolder(string title)
    {
#if UNITY_STANDALONE_WIN
        IFileOpenDialog dialog = null;
        IShellItem result = null;
        var namePtr = IntPtr.Zero;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR_FOD);
            dialog.SetTitle(title);

            IntPtr owner;
            try { owner = GetActiveWindow(); }
            catch { owner = IntPtr.Zero; }

            var hr = dialog.Show(owner);
            if (hr == HRESULT_CANCELLED)
                return null; // user cancelled
            if (hr != 0)
                return null; // any other non-success HRESULT — treated the same as cancel

            dialog.GetResult(out result);
            result.GetDisplayName(SIGDN_FILESYSPATH, out namePtr);
            return namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr) : null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SC2RuntimeFileDialog] native folder dialog failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
            if (result != null) Marshal.ReleaseComObject(result);
            if (dialog != null) Marshal.ReleaseComObject(dialog);
        }
#else
        Debug.LogWarning("[SC2RuntimeFileDialog] Native folder dialog is only implemented for Windows standalone builds.");
        return null;
#endif
    }
}
#endif
