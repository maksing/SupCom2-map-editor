using System.Windows.Forms;

namespace SC2InfoBdfEditorApp;

/// <summary>
/// "How to use this editor" — a real native WinForms dialog rather than an
/// ImGui popup, for the same reason field tooltips use a native ToolTip
/// (see MainForm.cs's own comment): this app's Dear ImGui build has no
/// working custom-font path, so its default font is permanently ASCII-only
/// and too small to comfortably read a long multi-section guide. A plain
/// read-only TextBox gets real Win32/GDI text rendering — full Unicode,
/// scrollable, selectable/copyable — for free.
/// </summary>
public sealed class HelpDialog : Form
{
    public HelpDialog()
    {
        Text = "How to Use This Editor";
        Width = 860;
        Height = 720;
        MinimumSize = new System.Drawing.Size(480, 360);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon; } catch { /* best-effort */ }

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Segoe UI", 10f),
            BackColor = System.Drawing.SystemColors.Window,
            Text = HelpText.Content.Replace("\n", "\r\n")
        };
        var closeButton = new Button
        {
            Text = "Close",
            Dock = DockStyle.Bottom,
            Height = 32
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(box);
        Controls.Add(closeButton);
        box.SelectionStart = 0;
        box.SelectionLength = 0;
    }
}
