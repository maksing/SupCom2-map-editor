using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The pure parts of the panel's editing tools: copy and paste of nodes, snapping and aligning, node descriptions and the comment frame.</summary>
public class MissionEditorToolsTests
{
    private static SC2MissionStage Stage()
    {
        var stage = new SC2MissionStage { Id = "stage1" };
        return stage;
    }

    private static SC2MissionNode Add(SC2MissionStage stage, string type, float x, float y, params (string key, string value)[] set)
    {
        var node = stage.AddNode(type, x, y);
        foreach (var (key, value) in set) node.SetParam(key, value);
        return node;
    }

    // ------------------------------------------------------------------ clipboard

    [Fact]
    public void CopyKeepsTheNodesAndOnlyTheWiresBetweenThem()
    {
        var stage = Stage();
        var a = Add(stage, "set_variable", 0, 0);
        var b = Add(stage, "set_variable", 100, 0);
        var c = Add(stage, "set_variable", 200, 0);
        stage.Connect(0, "out", a.Id);
        stage.Connect(a.Id, "out", b.Id);
        stage.Connect(b.Id, "out", c.Id);

        var clip = SC2MissionClipboard.Copy(stage, new[] { a.Id, b.Id });

        Assert.Equal(2, clip.Nodes.Count);
        Assert.Single(clip.Edges);
        Assert.Equal(a.Id, clip.Edges[0].FromNode);
        Assert.Equal(b.Id, clip.Edges[0].ToNode);
    }

    [Fact]
    public void PasteMakesNewNodesAtThePointAndWiresThemLikeTheOriginals()
    {
        var stage = Stage();
        var a = Add(stage, "set_variable", 40, 60, ("value", "7"));
        var b = Add(stage, "set_variable", 140, 60);
        stage.Connect(a.Id, "out", b.Id);
        var clip = SC2MissionClipboard.Copy(stage, new[] { a.Id, b.Id });
        var edgesBefore = stage.Edges.Count;

        var created = SC2MissionClipboard.Paste(stage, clip, 500, 300);

        Assert.Equal(2, created.Count);
        Assert.Equal(4, stage.Nodes.Count);
        Assert.All(created, n => Assert.DoesNotContain(n.Id, new[] { a.Id, b.Id }));
        Assert.Equal(500, created[0].X);
        Assert.Equal(300, created[0].Y);
        Assert.Equal(600, created[1].X);
        Assert.Equal("7", created[0].GetParam("value"));
        Assert.Equal(edgesBefore + 1, stage.Edges.Count);
        Assert.Contains(stage.Edges, e => e.FromNode == created[0].Id && e.ToNode == created[1].Id && e.FromPort == "out");
    }

    [Fact]
    public void PasteDoesNotChangeTheOriginals()
    {
        var stage = Stage();
        var a = Add(stage, "set_variable", 0, 0, ("value", "1"));
        var clip = SC2MissionClipboard.Copy(stage, new[] { a.Id });

        var created = SC2MissionClipboard.Paste(stage, clip, 10, 10);
        created[0].SetParam("value", "9");

        Assert.Equal("1", a.GetParam("value"));
    }

    [Fact]
    public void ALoopUnitNodeFollowsItsLoopWhenBothAreCopied_AndKeepsItWhenOnlyItIs()
    {
        var stage = Stage();
        var loop = Add(stage, "loop_foreach", 0, 0, ("army", "ARMY_2"), ("group", "G"));
        var kill = Add(stage, "loop_unit_kill", 300, 0, ("loop", loop.Id.ToString()));

        var both = SC2MissionClipboard.Paste(stage, SC2MissionClipboard.Copy(stage, new[] { loop.Id, kill.Id }), 0, 400);
        var onlyKill = SC2MissionClipboard.Paste(stage, SC2MissionClipboard.Copy(stage, new[] { kill.Id }), 0, 800);

        var newLoop = both.Single(n => n.Type == "loop_foreach");
        var newKill = both.Single(n => n.Type == "loop_unit_kill");
        Assert.Equal(newLoop.Id.ToString(), newKill.GetParam("loop"));
        Assert.Equal(loop.Id.ToString(), onlyKill[0].GetParam("loop"));
    }

    [Fact]
    public void AnEmptyClipPastesNothing()
    {
        var stage = Stage();

        Assert.Empty(SC2MissionClipboard.Paste(stage, new SC2MissionClip(), 0, 0));
        Assert.Empty(SC2MissionClipboard.Paste(stage, SC2MissionClipboard.Copy(stage, new int[0]), 0, 0));
    }

    // ------------------------------------------------------------------ snapping and aligning

    [Fact]
    public void SnapRoundsToTheGrid()
    {
        Assert.Equal(40f, SC2MissionNodeLayoutTools.Snap(47f));
        Assert.Equal(60f, SC2MissionNodeLayoutTools.Snap(51f));
        Assert.Equal(-20f, SC2MissionNodeLayoutTools.Snap(-14f));
        Assert.Equal(13f, SC2MissionNodeLayoutTools.Snap(13f, 0f));
    }

    [Fact]
    public void AlignAndDistributeOnlyMovePositions()
    {
        var stage = Stage();
        var a = Add(stage, "set_variable", 10, 30);
        var b = Add(stage, "set_variable", 50, 90);
        var c = Add(stage, "set_variable", 300, 10);
        var d = Add(stage, "set_variable", 120, 70);
        var all = new[] { a, b, c, d };

        SC2MissionNodeLayoutTools.AlignLeft(all);
        Assert.All(all, n => Assert.Equal(10, n.X));
        SC2MissionNodeLayoutTools.AlignTop(all);
        Assert.All(all, n => Assert.Equal(10, n.Y));

        a.X = 0; b.X = 30; c.X = 300; d.X = 90;
        SC2MissionNodeLayoutTools.DistributeHorizontally(all);
        Assert.Equal(new[] { 0f, 100f, 200f, 300f }, all.OrderBy(n => n.X).Select(n => n.X).ToArray());
        a.Y = 0; b.Y = 5; c.Y = 40; d.Y = 90;
        SC2MissionNodeLayoutTools.DistributeVertically(all);
        Assert.Equal(new[] { 0f, 30f, 60f, 90f }, all.OrderBy(n => n.Y).Select(n => n.Y).ToArray());
    }

    [Fact]
    public void AlignAndDistributeNeedEnoughNodes()
    {
        var stage = Stage();
        var a = Add(stage, "set_variable", 5, 5);
        var b = Add(stage, "set_variable", 9, 9);

        SC2MissionNodeLayoutTools.DistributeHorizontally(new[] { a, b });
        SC2MissionNodeLayoutTools.AlignLeft(new[] { a });

        Assert.Equal(5, a.X);
        Assert.Equal(9, b.X);
    }

    // ------------------------------------------------------------------ descriptions

    [Fact]
    public void EveryNodeHasADescription_AndTheCommonOnesHaveTheirOwn()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
            Assert.False(string.IsNullOrWhiteSpace(SC2MissionNodeDescriptions.Of(def)), def.Type + " has no description");

        foreach (var type in new[] { "timer", "spawn_group", "objective_kill", "dialogue", "branch", "victory", "loop_repeat", "ask_difficulty", "comment" })
            Assert.NotEqual(SC2MissionNodeDescriptions.Of(new SC2MissionNodeDef { Type = "x", Category = SC2MissionNodeCatalog.Find(type).Category }), SC2MissionNodeDescriptions.Of(SC2MissionNodeCatalog.Find(type)));
    }

    // ------------------------------------------------------------------ the comment frame

    [Fact]
    public void ACommentFrameWritesNoLuaAndIsNeverReportedAsUnreachable()
    {
        var doc = new SC2MissionDocument();
        var stage = Stage();
        var set = Add(stage, "set_variable", 0, 0);
        doc.Variables.Add(new SC2MissionVariable { Name = "n", Type = SC2MissionVariable.Number, Initial = "0" });
        set.SetParam("variable", "n");
        stage.Connect(0, "out", set.Id);
        var frame = Add(stage, "comment", -20, -20, ("text", "Setup"));
        doc.Stages.Add(stage);

        var result = SC2MissionCompiler.Compile(doc);

        Assert.DoesNotContain("N" + frame.Id, result.Lua);
        Assert.DoesNotContain(result.Issues, i => i.NodeId == frame.Id);
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error);
    }

    [Fact]
    public void ACommentFrameHasNoPorts()
    {
        var def = SC2MissionNodeCatalog.Find("comment");

        Assert.True(def.IsComment);
        Assert.Empty(SC2MissionNodeCatalog.InputsOf(def, null));
        Assert.Empty(SC2MissionNodeCatalog.PortsOf(def, null));
    }
}
