using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using BdfIO.Core;
using BdfIO.Formats.Info;
using SC2InfoBdfEditorApp;

namespace InfoBdfEditorTests;

/// <summary>
/// Exercises the ACTUAL production render pipeline (real MainForm, real
/// Direct3D 11 device/swapchain, real InfoBdfEditorUi.Draw) — not a mock —
/// against every real vanilla info.bdf in the corpus, cycling all 16 tabs
/// for each one. This is the test that would have caught both real bugs
/// found during development (the invisible tab strip, and the menu bar
/// rendering behind it) had it existed first: those were pure rendering/
/// layout bugs that a data-only test can never see, only a real render
/// pass can.
///
/// One shared off-screen window (see UiHostFixture) is reused across every
/// case instead of creating/destroying a D3D11 device per test — window
/// creation is the slow part, not rendering itself.
/// </summary>
public sealed class UiHostFixture : IDisposable
{
    public readonly MainForm Form;
    public readonly InfoBdfEditorUi Editor;
    private readonly MethodInfo _renderFrame;
    private readonly FieldInfo _modelField;
    private readonly FieldInfo _originalField;
    private readonly FieldInfo _loadedFilePathField;
    private readonly FieldInfo _loadedPlatformField;
    private readonly FieldInfo _selectedTabField;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    public UiHostFixture()
    {
        Editor = new InfoBdfEditorUi();
        Form = new MainForm(Editor.Draw)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Width = 1000,
            Height = 820
        };
        Editor.Host = Form;
        Form.Show();
        Form.Update(); // pumps Load synchronously so the D3D11 device exists now

        var editorType = typeof(InfoBdfEditorUi);
        _modelField = editorType.GetField("_model", Flags)!;
        _originalField = editorType.GetField("_original", Flags)!;
        _loadedFilePathField = editorType.GetField("_loadedFilePath", Flags)!;
        _loadedPlatformField = editorType.GetField("_loadedPlatform", Flags)!;
        _selectedTabField = editorType.GetField("_selectedTab", Flags)!;
        _renderFrame = typeof(MainForm).GetMethod("RenderFrame", Flags)!;
    }

    public void LoadModel(InfoModel model, string path)
    {
        var original = new InfoModel { Platform = model.Platform };
        Array.Copy(model.Header, original.Header, 508);
        original.EnvironmentMap = model.EnvironmentMap;
        original.SkyMap = model.SkyMap;
        original.ReservedTexturePath = model.ReservedTexturePath;
        original.ColorCorrectionCube = model.ColorCorrectionCube;
        original.FogOfWarCorrectionCube = model.FogOfWarCorrectionCube;

        _modelField.SetValue(Editor, model);
        _originalField.SetValue(Editor, original);
        _loadedFilePathField.SetValue(Editor, path);
        _loadedPlatformField.SetValue(Editor, model.Platform);
    }

    public void SelectTab(int index) => _selectedTabField.SetValue(Editor, index);

    /// <summary>Invokes the exact private method Application.Idle drives in
    /// the shipped app — same call, just triggered directly instead of
    /// waiting for the message pump to go idle.</summary>
    public void RenderOneFrame()
    {
        try
        {
            _renderFrame.Invoke(Form, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    public void Dispose() => Form.Dispose();
}

public sealed class UiRenderRobustnessTests : IClassFixture<UiHostFixture>
{
    private readonly UiHostFixture _fixture;

    public UiRenderRobustnessTests(UiHostFixture fixture) => _fixture = fixture;

    [Fact]
    public void EmptyStateRendersWithoutException()
    {
        var ex = Record.Exception(() => _fixture.RenderOneFrame());
        Assert.Null(ex);
    }

    public static IEnumerable<object[]> VanillaMaps() =>
        TestCorpus.IsAvailable
            ? TestCorpus.MapNames.Select(n => new object[] { n })
            : Array.Empty<object[]>();

    [Theory]
    [MemberData(nameof(VanillaMaps))]
    public void AllSixteenTabsRenderWithoutExceptionForRealFile(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.info.win.bdf");
        if (!File.Exists(path)) return;

        var raw = File.ReadAllBytes(path);
        var model = InfoBdf.Parse(BdfContainer.Read(raw, InfoBdf.Padding));
        _fixture.LoadModel(model, path);

        for (var tab = 0; tab < 16; tab++)
        {
            _fixture.SelectTab(tab);
            var ex = Record.Exception(() => _fixture.RenderOneFrame());
            Assert.True(ex == null, $"Tab {tab} threw for {mapName}: {ex}");
        }
    }

    [Fact]
    public void SwitchingEveryTabTwiceInARowIsStable()
    {
        // Catches ImGui ID-collision/state-leak bugs that only show up
        // across repeated open/close of the same CollapsingHeader or
        // repeated widget IDs being pushed/popped an unequal number of
        // times — a single pass wouldn't necessarily surface an unbalanced
        // PushID/PopID (it can silently "work" once and corrupt ImGui's ID
        // stack for the next frame instead of throwing immediately).
        for (var pass = 0; pass < 2; pass++)
            for (var tab = 0; tab < 16; tab++)
            {
                _fixture.SelectTab(tab);
                var ex = Record.Exception(() => _fixture.RenderOneFrame());
                Assert.True(ex == null, $"Pass {pass}, tab {tab}: {ex}");
            }
    }
}
