using System.Linq;

public sealed class PathGridTests
{
    [Fact]
    public void OpenGround_GivesAFullGridJoinedToAllEightNeighboursOnce()
    {
        var plan = SC2PathGrid.Build(0f, 0f, 96f, 96f, 32f, null);

        Assert.Equal(16, plan.Nodes.Count);
        Assert.Equal(4, plan.Columns);
        Assert.Equal(4, plan.Rows);
        // 4x4: 12 horizontal + 12 vertical + 9 + 9 diagonal pairs.
        Assert.Equal(42, plan.Edges.Count);
        Assert.Equal(plan.Edges.Count, plan.Edges.Select(e => e.A < e.B ? e : (e.B, e.A)).Distinct().Count());
        Assert.All(plan.Edges, e => Assert.NotEqual(e.A, e.B));
    }

    [Fact]
    public void TheGridIsCentredInTheRectangleOnWholeUnits()
    {
        var plan = SC2PathGrid.Build(0f, 0f, 100f, 100f, 30f, null);

        // 4 columns span 90 units, so the 10 left over are split on both sides.
        Assert.Equal(new[] { 5f, 35f, 65f, 95f }, plan.Nodes.Take(4).Select(n => n.x).ToArray());
        Assert.All(plan.Nodes, n => { Assert.Equal(n.x, UnityEngine.Mathf.Round(n.x)); Assert.Equal(n.y, UnityEngine.Mathf.Round(n.y)); });
    }

    [Fact]
    public void ImpassableNodesAreLeftOut_AndNothingIsJoinedToThem()
    {
        // A wall across the middle column.
        var plan = SC2PathGrid.Build(0f, 0f, 64f, 64f, 32f, (x, z) => x != 32f);

        Assert.Equal(6, plan.Nodes.Count);
        Assert.All(plan.Nodes, n => Assert.NotEqual(32f, n.x));
        // The two side columns are 64 apart, so only the vertical pairs inside each of them remain (2 per column).
        Assert.Equal(4, plan.Edges.Count);
    }

    [Fact]
    public void AWayThroughImpassableGround_IsNotJoinedEvenWhenBothEndsAreFree()
    {
        // Nodes at x = 0 and 64 are passable, the strip between them is not.
        var plan = SC2PathGrid.Build(0f, 0f, 64f, 0f, 64f, (x, z) => x <= 0f || x >= 64f);

        Assert.Equal(2, plan.Nodes.Count);
        Assert.Empty(plan.Edges);
    }

    [Fact]
    public void AStepFinerThanTheCapRefusesToPlanAnything()
    {
        var plan = SC2PathGrid.Build(0f, 0f, 1024f, 1024f, 4f, null);

        Assert.True(plan.TooManyNodes);
        Assert.Empty(plan.Nodes);
        Assert.Empty(plan.Edges);
    }

    [Fact]
    public void ARectangleSmallerThanOneStep_StillGetsASingleNode()
    {
        var plan = SC2PathGrid.Build(10f, 10f, 20f, 20f, 64f, null);

        Assert.Single(plan.Nodes);
        Assert.Empty(plan.Edges);
    }
}
