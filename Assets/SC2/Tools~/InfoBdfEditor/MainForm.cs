using System.Windows.Forms;
using Hexa.NET.D3DCommon;
using Hexa.NET.DXGI;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using D3D11Native = Hexa.NET.D3D11;

namespace SC2InfoBdfEditorApp;

/// <summary>
/// Hosts a genuinely independent Win32 window (own taskbar icon,
/// minimizable/movable independently of Unity) whose entire client area is
/// drawn by Dear ImGui over Direct3D 11 — no WinForms Control is ever
/// placed on this Form; it exists purely to obtain a real HWND and run its
/// message pump. See SC2InfoBdfEditorApp.csproj's header comment for why
/// Win32+D3D11 was chosen over the GLFW+OpenGL combination initially
/// attempted (a native crash traced to Dear ImGui's per-backend-DLL
/// context pointer not propagating automatically).
/// </summary>
public sealed unsafe class MainForm : Form
{
    private D3D11Native.ID3D11Device* _device;
    private D3D11Native.ID3D11DeviceContext* _context;
    private IDXGISwapChain* _swapChain;
    private D3D11Native.ID3D11RenderTargetView* _rtv;
    private ImGuiContextPtr _imguiContext;

    private readonly Action _drawUi;
    private bool _resizePending;

    // Real Win32 tooltip (GDI text rendering) used specifically for field
    // help instead of ImGui.SetTooltip — this app's Dear ImGui build
    // (3.1.0-experimental) has no working custom-font path (three separate
    // attempts at loading a bigger font all rendered a
    // totally blank window), so its default font is
    // permanently stuck at ASCII-only Proggy Clean, too small to comfortably
    // read a multi-line field description. A native ToolTip sidesteps the ImGui font system
    // entirely — real Windows text rendering, full Unicode support
    // guaranteed, using a WinForms component this app already relies on
    // elsewhere (OpenFileDialog/MessageBox). Shown/hidden by clicking a "?"
    // button next to each field (not on hover — calling Show() every frame
    // while hovering made the tooltip visibly flicker), so there is no
    // implicit auto-pop deadline to race against.
    //
    // OwnerDraw + a fixed GraphicsUnit.Pixel font: the OS's default tooltip
    // font is sized in POINTS, which Windows scales by the display's DPI
    // setting — at 150% scaling a 9pt font renders about 1.5x larger than
    // its "design" size, visibly oversized next
    // to the rest of this app's UI. Everything else on screen is drawn by
    // ImGui as a fixed-pixel bitmap font that Windows' DPI scaling never
    // touches at all, so the two fonts drifted apart under any scaling
    // other than 100%. GraphicsUnit.Pixel sidesteps that entirely — this
    // font is always exactly TooltipFontSize real pixels tall, matching
    // the rest of the UI's own fixed-pixel sizing regardless of the
    // user's Windows display-scaling setting. OwnerDraw also replaces the
    // separate TTM_SETMAXTIPWIDTH/EnumWindows Win32-message plumbing an
    // earlier version of this fix needed — with OwnerDraw, this app
    // already fully controls both the measured size (Popup) and the
    // rendering (Draw), so there's no longer a second, independent native
    // control size to keep in sync with what was measured.
    private const float TooltipFontSize = 13f;
    private static readonly System.Drawing.Font TooltipFont = new("Segoe UI", TooltipFontSize, System.Drawing.GraphicsUnit.Pixel);
    private string _tooltipText = "";

    private readonly ToolTip _nativeTooltip = new()
    {
        ShowAlways = true,
        UseFading = false,
        UseAnimation = false,
        OwnerDraw = true,
        AutoPopDelay = 32767, // effectively "don't auto-hide" — this app calls HideNativeTooltip() explicitly (click the same "?" again, click a different one, or switch tabs)
        InitialDelay = 0,
        ReshowDelay = 0
    };

    private static System.Drawing.Size MeasureTooltip(string text, int maxWidth) =>
        System.Windows.Forms.TextRenderer.MeasureText(text, TooltipFont, new System.Drawing.Size(maxWidth, int.MaxValue), System.Windows.Forms.TextFormatFlags.WordBreak);

    private void InitNativeTooltip()
    {
        _nativeTooltip.Popup += (_, e) =>
        {
            var maxWidth = Math.Max(200, ClientSize.Width - 24);
            var size = MeasureTooltip(_tooltipText, maxWidth);
            e.ToolTipSize = new System.Drawing.Size(size.Width + 8, size.Height + 8);
        };
        _nativeTooltip.Draw += (_, e) =>
        {
            e.Graphics.FillRectangle(System.Drawing.SystemBrushes.Window, e.Bounds);
            e.DrawBorder();
            var textBounds = new System.Drawing.Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, e.Bounds.Width - 8, e.Bounds.Height - 8);
            System.Windows.Forms.TextRenderer.DrawText(e.Graphics, _tooltipText, TooltipFont, textBounds,
                System.Drawing.SystemColors.WindowText, System.Windows.Forms.TextFormatFlags.WordBreak | System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.Top);
        };
    }

    // Screen-space bounds of the currently-shown tooltip, tracked so the
    // render loop can tell whether the cursor has wandered off it — a
    // native ToolTip has no MouseLeave-style event of its own to hook, so
    // this app polls Cursor.Position against these bounds once per frame
    // instead (see ShouldAutoCloseTooltip).
    private System.Drawing.Rectangle _tooltipScreenBounds;
    private bool _tooltipVisible;

    /// <summary>Shows the tooltip at (clientX, clientY), constrained to
    /// stay fully inside this window's client area — without this, a long
    /// description positioned near the right/bottom edge extends past the
    /// window onto the desktop (the native ToolTip is a top-level popup
    /// that Windows only keeps on-SCREEN, not inside whatever control owns
    /// it).</summary>
    public void ShowNativeTooltip(string text, int clientX, int clientY)
    {
        _tooltipText = text;
        var maxWidth = Math.Max(200, ClientSize.Width - 24);
        var size = MeasureTooltip(text, maxWidth);
        var maxX = Math.Max(0, ClientSize.Width - size.Width - 16);
        var maxY = Math.Max(0, ClientSize.Height - size.Height - 16);
        var x = Math.Clamp(clientX, 0, maxX);
        var y = Math.Clamp(clientY, 0, maxY);
        var tooltipSize = new System.Drawing.Size(size.Width + 8, size.Height + 8);
        _tooltipScreenBounds = new System.Drawing.Rectangle(PointToScreen(new System.Drawing.Point(x, y)), tooltipSize);
        _tooltipVisible = true;
        _nativeTooltip.Show(text, this, x, y);
    }

    public void HideNativeTooltip()
    {
        _tooltipVisible = false;
        _nativeTooltip.Hide(this);
    }

    /// <summary>Queues the "how to use this editor" guide to open right
    /// after the CURRENT render frame is fully done, rather than opening
    /// it immediately. Calling ShowDialog() synchronously from inside
    /// _drawUi() — as an earlier version of this did — pumps a nested
    /// Windows message loop while ImGui.NewFrame() has already been
    /// called but ImGui.Render() has not; Application.Idle re-fires
    /// inside that nested pump, re-entering RenderFrame() and therefore
    /// _drawUi()/ImGui.NewFrame() a second time before the FIRST frame
    /// finished — corrupting ImGui's internal window/ID stack and
    /// crashing the native side (observed by the user as "the whole
    /// editor just closes"). BeginInvoke posts this to the form's own
    /// message queue instead, so it only actually runs once the current
    /// call stack (however deep inside Draw() it started) has fully
    /// unwound and control is back at the top-level message pump.</summary>
    public void RequestHelpDialog() => BeginInvoke(new Action(ShowHelpDialogNow));

    private void ShowHelpDialogNow()
    {
        using var dlg = new HelpDialog();
        dlg.ShowDialog(this);
    }

    /// <summary>True once the cursor is outside BOTH the tooltip's own
    /// bounds and <paramref name="keepOpenScreenRect"/> (the "?" button
    /// that opened it, in screen coordinates) — the "safe zone" the
    /// tooltip should stay open across. Without also covering the
    /// button's own rect, the tooltip would auto-close on the very frame
    /// it's opened, since the cursor sits on the button (not yet over the
    /// tooltip itself) at the moment of the click.</summary>
    public bool ShouldAutoCloseTooltip(System.Drawing.Rectangle keepOpenScreenRect)
    {
        if (!_tooltipVisible) return false;
        var pos = Cursor.Position;
        return !_tooltipScreenBounds.Contains(pos) && !keepOpenScreenRect.Contains(pos);
    }

    public MainForm(Action drawUi)
    {
        _drawUi = drawUi;

        Text = "SC2 Info.bdf Editor";
        MinimumSize = new System.Drawing.Size(700, 500);
        // The exe's own icon (embedded via <ApplicationIcon> in the .csproj)
        // isn't picked up by a Form automatically — WinForms shows a generic
        // default icon unless Form.Icon is set explicitly. Pull it straight
        // back out of the running exe rather than shipping/loading a second
        // loose .ico file next to it.
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon; } catch { /* best-effort */ }
        InitNativeTooltip();

        var settings = AppSettings.Load();
        Width = settings.WindowWidth > 0 ? settings.WindowWidth : 1000;
        Height = settings.WindowHeight > 0 ? settings.WindowHeight : 820;
        if (settings.WindowX != int.MinValue && settings.WindowY != int.MinValue && IsPositionOnAnyScreen(settings.WindowX, settings.WindowY))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(settings.WindowX, settings.WindowY);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        Load += (_, _) => InitGraphics();
        FormClosed += (_, _) => Shutdown();
        Resize += (_, _) => _resizePending = true;
    }

    /// <summary>Guards against restoring a window position that made sense
    /// on a monitor layout that's no longer connected (a second monitor
    /// unplugged since the last run, etc.) — falls back to CenterScreen
    /// rather than opening the window somewhere entirely off-screen.</summary>
    private static bool IsPositionOnAnyScreen(int x, int y)
    {
        foreach (var screen in Screen.AllScreens)
            if (screen.WorkingArea.Contains(x, y)) return true;
        return false;
    }

    private void InitGraphics()
    {
        CreateDeviceAndSwapChain();
        CreateRenderTarget();

        _imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiContext);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        // Disable ImGui's default "write imgui.ini next to the current
        // working directory" behavior — this app can be launched with an
        // arbitrary CWD (e.g. from inside the Unity project itself during
        // testing), and a stray imgui.ini has no real settings this data
        // editor needs to persist (no dockable panels/movable windows).
        io.IniFilename = null;
        Theme.Apply(ImGui.GetStyle());

        // Each ImGuiImplXxx backend is a separate native module with its own
        // internal context pointer — SetCurrentContext must be called on
        // EVERY one of them individually, or their Init() dereferences a
        // null/garbage context (confirmed via a real access-violation crash
        // during development; see the .csproj header comment).
        ImGuiImplWin32.SetCurrentContext(_imguiContext);
        ImGuiImplWin32.Init(Handle);
        ImGuiImplD3D11.SetCurrentContext(_imguiContext);
        ImGuiImplD3D11.Init(
            new ID3D11DevicePtr((ID3D11Device*)_device),
            new ID3D11DeviceContextPtr((ID3D11DeviceContext*)_context));

        Application.Idle += OnIdle;
    }

    private void CreateDeviceAndSwapChain()
    {
        var desc = new SwapChainDesc
        {
            BufferCount = 2,
            BufferDesc = new ModeDesc
            {
                Width = (uint)Math.Max(1, ClientSize.Width),
                Height = (uint)Math.Max(1, ClientSize.Height),
                Format = Format.B8G8R8A8Unorm,
                RefreshRate = new Rational(60, 1)
            },
            BufferUsage = (uint)DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            OutputWindow = Handle,
            SampleDesc = new SampleDesc(1, 0),
            Windowed = 1,
            SwapEffect = SwapEffect.Discard
        };

        D3D11Native.ID3D11Device* device = null;
        D3D11Native.ID3D11DeviceContext* context = null;
        IDXGISwapChain* swapChain = null;
        FeatureLevel featureLevel = default;

        var hr = D3D11Native.D3D11.CreateDeviceAndSwapChain(
            null, DriverType.Hardware, IntPtr.Zero, 0,
            null, 0, D3D11Native.D3D11.D3D11_SDK_VERSION,
            ref desc, ref swapChain, ref device, ref featureLevel, ref context);

        if (hr.IsError)
        {
            // WARP (software rasterizer) fallback — always present on
            // Windows, unlike a real GPU driver, so this is the honest
            // "works on any Windows machine" guarantee for end users whose
            // hardware/driver situation we can't predict.
            hr = D3D11Native.D3D11.CreateDeviceAndSwapChain(
                null, DriverType.Warp, IntPtr.Zero, 0,
                null, 0, D3D11Native.D3D11.D3D11_SDK_VERSION,
                ref desc, ref swapChain, ref device, ref featureLevel, ref context);
        }

        if (hr.IsError)
            throw new InvalidOperationException($"Direct3D 11 device creation failed (0x{hr.Value:X8}) — no hardware or WARP software device available.");

        _device = device;
        _context = context;
        _swapChain = swapChain;
    }

    private void CreateRenderTarget()
    {
        D3D11Native.ID3D11Texture2D* backBuffer;
        var texGuid = D3D11Native.ID3D11Texture2D.Guid;
        _swapChain->GetBuffer(0, &texGuid, (void**)&backBuffer);
        fixed (D3D11Native.ID3D11RenderTargetView** rtvPtr = &_rtv)
            _device->CreateRenderTargetView((D3D11Native.ID3D11Resource*)backBuffer, null, rtvPtr);
        backBuffer->Release();
    }

    private void ReleaseRenderTarget()
    {
        if (_rtv != null) { _rtv->Release(); _rtv = null; }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        // Render continuously while the message queue is empty — the
        // standard WinForms "game loop" pattern. This is a data/settings
        // editor, not a game, so there's no need for a tighter frame-timed
        // loop; rendering exactly when idle keeps CPU usage reasonable
        // without any manual throttling.
        //
        // Must break the INSTANT a message is pending, not after — a prior
        // version broke only when NO message was pending (inverted), which
        // meant that as soon as any message started arriving (e.g. mouse
        // movement generates a steady stream of WM_MOUSEMOVE), this loop
        // never yielded back to Application.Run's real GetMessage/
        // DispatchMessage pump. Confirmed via a real user report: clicking
        // File or the title bar's minimize/close buttons did nothing,
        // because those clicks were queued as pending messages but never
        // actually dispatched while this loop kept spinning.
        while (IsHandleCreated && !IsDisposed && !NativeMessagePending())
        {
            if (_resizePending) { HandleResize(); _resizePending = false; }
            RenderFrame();
        }
    }

    // PeekMessage-based "is there a message waiting" check so the idle loop
    // yields back to the real message pump instead of spinning forever.
    private static bool NativeMessagePending() => System.Windows.Forms.Application.MessageLoop && PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessageStruct msg, IntPtr hWnd, uint msgFilterMin, uint msgFilterMax, uint removeMsg);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMessageStruct
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    private void HandleResize()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        ReleaseRenderTarget();
        _swapChain->ResizeBuffers(0, (uint)ClientSize.Width, (uint)ClientSize.Height, Format.Unknown, 0);
        CreateRenderTarget();
    }

    private void RenderFrame()
    {
        ImGuiImplD3D11.NewFrame();
        ImGuiImplWin32.NewFrame();
        ImGui.NewFrame();

        ImGui.SetNextWindowPos(default);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(ClientSize.Width, ClientSize.Height));
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar;
        ImGui.Begin("##root", flags);
        try { _drawUi(); }
        finally { ImGui.End(); }

        ImGui.Render();

        fixed (D3D11Native.ID3D11RenderTargetView** rtvPtr = &_rtv)
            _context->OMSetRenderTargets(1, rtvPtr, (D3D11Native.ID3D11DepthStencilView*)null);
        var clearColor = stackalloc float[4] { Theme.Bg.X, Theme.Bg.Y, Theme.Bg.Z, 1f };
        _context->ClearRenderTargetView(_rtv, clearColor);
        ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
        _swapChain->Present(1, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (_imguiContext.Handle != null)
            ImGuiImplWin32.WndProcHandler(m.HWnd, (uint)m.Msg, (nuint)(nint)m.WParam, m.LParam);
        base.WndProc(ref m);
    }

    private void Shutdown()
    {
        Application.Idle -= OnIdle;
        _nativeTooltip.Dispose();
        if (_imguiContext.Handle == null) return;

        ImGuiImplD3D11.Shutdown();
        ImGuiImplWin32.Shutdown();
        ImGui.DestroyContext(_imguiContext);
        _imguiContext = default;

        ReleaseRenderTarget();
        if (_swapChain != null) { _swapChain->Release(); _swapChain = null; }
        if (_context != null) { _context->Release(); _context = null; }
        if (_device != null) { _device->Release(); _device = null; }
    }
}
