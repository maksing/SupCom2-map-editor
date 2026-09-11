using SC2.MapEditor.SmartExport;
using System.Linq;
using System.Text;

/// <summary>
/// Purely synthetic,
/// no vanilla fixture data needed. Pins down the dependency-graph expansion
/// and planner disposition rules, so a
/// SC2MapDocument migration that reuses or replaces this logic has a
/// baseline to diff against.
/// </summary>
public class SmartExportTests
{
    [Fact]
    public void ExpandStaleFlags_TerrainGeometryPropagatesToHfieldCostsCollision()
    {
        var expanded = SC2ExportDependencyGraph.ExpandStaleFlags(SC2DirtyFlags.TerrainGeometry);

        Assert.True((expanded & SC2DirtyFlags.TerrainGeometry) != 0);
        Assert.True((expanded & SC2DirtyFlags.HfieldHeightmap) != 0);
        Assert.True((expanded & SC2DirtyFlags.Costs) != 0);
        Assert.True((expanded & SC2DirtyFlags.Collision2) != 0);
        // TerrainGeometry does NOT directly imply Objects/Info/Lua dirty.
        Assert.False((expanded & SC2DirtyFlags.Objects) != 0);
    }

    [Fact]
    public void ExpandStaleFlags_ObjectsPropagatesToSaveAndScenarioLuaOnly()
    {
        var expanded = SC2ExportDependencyGraph.ExpandStaleFlags(SC2DirtyFlags.Objects);

        Assert.True((expanded & SC2DirtyFlags.SaveLua) != 0);
        Assert.True((expanded & SC2DirtyFlags.ScenarioLua) != 0);
        Assert.False((expanded & SC2DirtyFlags.ScriptLua) != 0);
        Assert.False((expanded & SC2DirtyFlags.Costs) != 0);
    }

    [Fact]
    public void BuildPlan_UnchangedFileGoesPassthrough()
    {
        var file = SC2SmartExportPlanner.CreateOriginal(SC2ExportFileKind.TerrainBdf, "terrain.bdf", new byte[] { 1, 2, 3 });
        var plan = SC2SmartExportPlanner.BuildPlan(new[] { file }, SC2DirtyFlags.None);

        var result = plan.Find(SC2ExportFileKind.TerrainBdf);
        Assert.Equal(SC2ExportFileMode.OriginalPassthrough, result.Mode);
        Assert.False(plan.HasBlockingErrors());
    }

    [Fact]
    public void BuildPlan_DirtyFileWithoutRegeneratedBytesIsBlocked()
    {
        // Characterizes audit finding P0.3: the foundation models this
        // correctly (a dirty file with no output is an error), but
        // SC2TerrainExporter never actually consults SC2DirtyFlags, so in
        // practice every file is unconditionally regenerated regardless of
        // what this planner would say. This test is about the planner in
        // isolation, not about whether the real exporter calls it (it
        // currently doesn't — see the audit).
        var file = SC2SmartExportPlanner.CreateOriginal(SC2ExportFileKind.CostsBdf, "costs.bdf", new byte[] { 9 });
        var plan = SC2SmartExportPlanner.BuildPlan(new[] { file }, SC2DirtyFlags.Costs);

        var result = plan.Find(SC2ExportFileKind.CostsBdf);
        Assert.True(result.HasErrors);
        Assert.True(plan.HasBlockingErrors());
    }

    [Fact]
    public void BuildPlan_RegeneratedFileWithOutputIsAccepted()
    {
        var file = SC2SmartExportPlanner.CreateOriginal(SC2ExportFileKind.CostsBdf, "costs.bdf", new byte[] { 9 });
        file.SetOutput(new byte[] { 1, 2 }, SC2ExportFileMode.Regenerated);
        var plan = SC2SmartExportPlanner.BuildPlan(new[] { file }, SC2DirtyFlags.Costs);

        var result = plan.Find(SC2ExportFileKind.CostsBdf);
        Assert.Equal(SC2ExportFileMode.Regenerated, result.Mode);
        Assert.False(plan.HasBlockingErrors());
    }

    [Fact]
    public void BuildPlan_StaleDependentFileGetsMarkedWithReason()
    {
        // TerrainGeometry dirty -> Collision2 becomes stale even though it
        // wasn't itself directly touched.
        var file = SC2SmartExportPlanner.CreateOriginal(SC2ExportFileKind.Collision2Bdf, "collision2.bdf", new byte[] { 9 });
        var plan = SC2SmartExportPlanner.BuildPlan(new[] { file }, SC2DirtyFlags.TerrainGeometry);

        var result = plan.Find(SC2ExportFileKind.Collision2Bdf);
        Assert.True(result.IsStale);
        Assert.Contains(result.Warnings, w => w.Contains("collision2 depends on hfield"));
    }

    /// <summary>
    /// Characterizes SC2ExportFileState.NormalizeRelativePath: it strips
    /// leading slashes and flips backslashes, but does NOT reject "../"
    /// path-traversal segments. This behavior is superseded by SC2SafePath
    /// (which does reject them) for archive entry paths — this test pins
    /// down NormalizeRelativePath's own remaining behavior, which
    /// SC2SafePath's own doc comment cites as evidence of the gap it closes.
    /// </summary>
    [Fact]
    public void NormalizeRelativePath_DoesNotRejectParentTraversal_CurrentBehavior()
    {
        var normalized = SC2ExportFileState.NormalizeRelativePath("../../outside/evil.bdf");
        Assert.Contains("..", normalized); // documents that traversal segments currently survive normalization unchanged
    }
}
