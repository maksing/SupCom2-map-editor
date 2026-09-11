using System.IO;
using System.Linq;
using System.Reflection;
using BdfIO.Core;
using BdfIO.Formats.Info;
using SC2InfoBdfEditorApp;

namespace InfoBdfEditorTests;

/// <summary>
/// Undo/redo and "Import Values From..." are pure in-memory logic with no
/// UI dependency, but they're also the easiest kind of bug to introduce
/// silently (off-by-one on when a snapshot gets pushed, a stale redo entry
/// surviving a divergent edit, a category copying the wrong fields) — all
/// private, so driven via reflection the same way UiRenderRobustnessTests
/// already does for MainForm's private render pipeline.
/// </summary>
public sealed class UndoRedoImportTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type EditorType = typeof(InfoBdfEditorUi);

    private static T GetField<T>(InfoBdfEditorUi ui, string name) => (T)EditorType.GetField(name, Flags)!.GetValue(ui)!;
    private static void SetField(InfoBdfEditorUi ui, string name, object? value) => EditorType.GetField(name, Flags)!.SetValue(ui, value);
    private static void Invoke(InfoBdfEditorUi ui, string name, params object[] args) => EditorType.GetMethod(name, Flags)!.Invoke(ui, args);

    private static InfoModel CloneModel(InfoModel src)
    {
        var method = EditorType.GetMethod("CloneModel", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (InfoModel)method.Invoke(null, new object[] { src })!;
    }

    /// <summary>Sets up an editor as if a file were already open and clean
    /// (no pending edits) — the state every real edit/undo/redo action
    /// starts from.</summary>
    private static InfoBdfEditorUi CreateOpenedEditor(float initialSunIntensity)
    {
        var ui = new InfoBdfEditorUi();
        var model = new InfoModel { Platform = BdfPlatform.Pc, SunIntensity = initialSunIntensity };
        SetField(ui, "_model", model);
        SetField(ui, "_original", CloneModel(model));
        SetField(ui, "_loadedFilePath", "dummy.info.win.bdf");
        Invoke(ui, "RefreshCleanSnapshot");
        return ui;
    }

    private static InfoModel Model(InfoBdfEditorUi ui) => GetField<InfoModel>(ui, "_model");
    private static int UndoCount(InfoBdfEditorUi ui) => GetField<Stack<InfoModel>>(ui, "_undoStack").Count;
    private static int RedoCount(InfoBdfEditorUi ui) => GetField<Stack<InfoModel>>(ui, "_redoStack").Count;

    [Fact]
    public void SingleEditPushesExactlyOneUndoEntry()
    {
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 2f;
        Invoke(ui, "MarkDirty");

        Assert.Equal(1, UndoCount(ui));
        Assert.Equal(0, RedoCount(ui));
    }

    [Fact]
    public void EditsWithinTheSameDirtyBurstCoalesceIntoOneUndoEntry()
    {
        // Mirrors a slider drag: many MarkDirty() calls happen before the
        // debounced auto-save ever resets _dirty back to false — only the
        // FIRST one in the burst should open a new undo entry.
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 2f;
        Invoke(ui, "MarkDirty");
        Model(ui).SunIntensity = 3f;
        Invoke(ui, "MarkDirty");
        Model(ui).SunIntensity = 4f;
        Invoke(ui, "MarkDirty");

        Assert.Equal(1, UndoCount(ui));
    }

    [Fact]
    public void UndoRestoresThePreBurstValueAndPopulatesRedo()
    {
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 2f;
        Invoke(ui, "MarkDirty");
        Model(ui).SunIntensity = 3f;
        Invoke(ui, "MarkDirty");

        Invoke(ui, "Undo");

        Assert.Equal(1f, Model(ui).SunIntensity);
        Assert.Equal(0, UndoCount(ui));
        Assert.Equal(1, RedoCount(ui));
    }

    [Fact]
    public void RedoReappliesTheUndoneValue()
    {
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 2f;
        Invoke(ui, "MarkDirty");
        Invoke(ui, "Undo");

        Invoke(ui, "Redo");

        Assert.Equal(2f, Model(ui).SunIntensity);
        Assert.Equal(1, UndoCount(ui));
        Assert.Equal(0, RedoCount(ui));
    }

    [Fact]
    public void UndoThenFreshEditDropsTheNowInvalidRedoEntry()
    {
        // Regression test for the exact bug _boundaryPending exists to
        // prevent: without it, editing right after an Undo (instead of
        // Redo-ing) would leave a stale, unreachable entry sitting in
        // _redoStack forever instead of being cleared like every other
        // real editor's undo/redo does when history diverges.
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 2f;
        Invoke(ui, "MarkDirty");
        Invoke(ui, "Undo"); // back to 1, redoStack now holds [2]
        Assert.Equal(1, RedoCount(ui));

        Model(ui).SunIntensity = 5f; // a genuinely new, divergent edit
        Invoke(ui, "MarkDirty");

        Assert.Equal(0, RedoCount(ui));
        Assert.Equal(1, UndoCount(ui)); // the pre-undo-restored value (1) is still undoable
    }

    [Fact]
    public void RevertAllRestoresOriginalAndIsItselfUndoable()
    {
        var ui = CreateOpenedEditor(1f);
        Model(ui).SunIntensity = 9f;
        Invoke(ui, "MarkDirty"); // an edit is now pending (dirty=true), not yet auto-saved

        Invoke(ui, "RevertAll");
        Assert.Equal(1f, Model(ui).SunIntensity);

        Invoke(ui, "Undo");
        // Must recover the exact in-progress value (9), not just the last
        // clean/saved boundary — regression test for PushUndoBoundary:
        // RevertAll must capture the pre-revert state unconditionally,
        // since MarkDirty()'s own coalescing would otherwise skip the push
        // here (an edit was already pending) and silently lose it.
        Assert.Equal(9f, Model(ui).SunIntensity);
    }

    private static Array GetImportFields() => (Array)EditorType.GetField("ImportFields", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private static string FieldCategory(object f) => (string)f.GetType().GetField("Item1")!.GetValue(f)!;
    private static string FieldLabel(object f) => (string)f.GetType().GetField("Item2")!.GetValue(f)!;
    private static Action<InfoModel, InfoModel> FieldCopy(object f) => (Action<InfoModel, InfoModel>)f.GetType().GetField("Item3")!.GetValue(f)!;

    [Fact]
    public void SingleWaterFieldImportCopiesOnlyThatFieldNotSunOrOtherWaterFields()
    {
        var fields = GetImportFields().Cast<object>().ToArray();
        var elevationEntry = fields.Single(f => FieldCategory(f) == "Water" && FieldLabel(f) == "WaterElevation");
        var copy = FieldCopy(elevationEntry);

        var src = new InfoModel { Platform = BdfPlatform.Pc, WaterElevation = 42f, MaxWaterDepth = 77f, SunIntensity = 99f };
        var dst = new InfoModel { Platform = BdfPlatform.Pc, WaterElevation = 0f, MaxWaterDepth = 1f, SunIntensity = 1f };

        copy(src, dst);

        Assert.Equal(42f, dst.WaterElevation);
        Assert.Equal(1f, dst.MaxWaterDepth); // untouched — a single-field import must not pull in sibling Water fields
        Assert.Equal(1f, dst.SunIntensity); // untouched — must not leak into Sun's fields
    }

    [Fact]
    public void AllImportFieldsAreGroupedContiguouslyByCategoryInTabTitleOrder()
    {
        // DrawImportModal groups consecutive same-category runs while
        // walking the array once — if a category's rows were ever split
        // into two non-adjacent runs, that grouping logic would silently
        // render it as two separate collapsing headers with the same name.
        var fields = GetImportFields().Cast<object>().ToArray();
        var tabTitles = (string[])EditorType.GetField("TabTitles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var seenCategories = new HashSet<string>();
        var distinctInOrder = new List<string>();
        foreach (var f in fields)
        {
            var cat = FieldCategory(f);
            if (distinctInOrder.Count == 0 || distinctInOrder[^1] != cat)
            {
                Assert.True(seenCategories.Add(cat), $"Category '{cat}' appears in more than one non-adjacent run.");
                distinctInOrder.Add(cat);
            }
        }
        Assert.Equal(tabTitles, distinctInOrder);
    }

    [Fact]
    public void WriteOpenBackupWritesAByteIdenticalDotBakNextToTheOpenedFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"infobdf_backup_test_{Guid.NewGuid():N}.info.win.bdf");
        var backupPath = tempPath + ".bak";
        var bytes = new byte[] { 1, 2, 3, 4, 250, 251, 252 };
        try
        {
            var method = EditorType.GetMethod("WriteOpenBackup", BindingFlags.NonPublic | BindingFlags.Static)!;
            method.Invoke(null, new object[] { tempPath, bytes });

            Assert.True(File.Exists(backupPath));
            Assert.Equal(bytes, File.ReadAllBytes(backupPath));
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
