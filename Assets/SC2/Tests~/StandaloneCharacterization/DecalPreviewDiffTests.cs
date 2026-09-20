using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>Which decal placements an edit forces the preview to rebuild: only the ones whose
/// own data changed, so placing or editing one decal on a large map stays cheap.</summary>
public class DecalPreviewDiffTests
{
    private static SC2DecalsDocument Document(int count)
    {
        var d = new SC2DecalsDocument { AtlasWidth = 512, AtlasHeight = 512 };
        d.Entries.Add(new SC2DecalAtlasEntry { Id = "e", Name = "Road", X = 0, Y = 0, Width = 64, Height = 64 });
        for (var i = 0; i < count; i++)
            d.Placements.Add(new SC2DecalPlacement { Id = "p" + i, AtlasEntryId = "e", Position = new[] { i * 10f, 1f, 5f } });
        return d;
    }

    private static Dictionary<string, SC2DecalPlacement> BuiltFrom(SC2DecalsDocument d) =>
        d.Placements.ToDictionary(p => p.Id, p => p.Clone());

    [Fact]
    public void UnchangedDocument_RebuildsNothing()
    {
        var d = Document(50);
        var result = SC2DecalPreviewDiff.Compute(d, BuiltFrom(d));
        Assert.Empty(result.Rebuild);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void AddingOnePlacement_RebuildsOnlyThatOne()
    {
        var d = Document(50);
        var built = BuiltFrom(d);
        d.Placements.Add(new SC2DecalPlacement { Id = "new", AtlasEntryId = "e" });
        var result = SC2DecalPreviewDiff.Compute(d, built);
        Assert.Equal(new[] { "new" }, result.Rebuild.ToArray());
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void EditingOnePlacement_RebuildsOnlyThatOne()
    {
        var d = Document(50);
        var built = BuiltFrom(d);
        d.Placements[7].Rotation = 30f;
        d.Placements[9].Position[0] += 0.5f;
        d.Placements[11].Enabled = false;
        var result = SC2DecalPreviewDiff.Compute(d, built);
        Assert.Equal(new HashSet<string> { "p7", "p9", "p11" }, result.Rebuild);
    }

    [Fact]
    public void DeletingAPlacement_ReportsItAsRemoved_AndRebuildsNothingElse()
    {
        var d = Document(50);
        var built = BuiltFrom(d);
        d.Placements.RemoveAt(3);
        var result = SC2DecalPreviewDiff.Compute(d, built);
        Assert.Empty(result.Rebuild);
        Assert.Equal(new[] { "p3" }, result.Removed.ToArray());
    }

    [Fact]
    public void ReorderingPlacements_RebuildsNothing()
    {
        var d = Document(10);
        var built = BuiltFrom(d);
        d.Placements.Reverse();
        Assert.Empty(SC2DecalPreviewDiff.Compute(d, built).Rebuild);
    }

    [Fact]
    public void EntriesSignature_ChangesWithAtlasRectangleButNotWithNameOrDefaults()
    {
        var d = Document(1);
        var before = SC2DecalPreviewDiff.EntriesSignature(d);
        d.Entries[0].Name = "Renamed";
        d.Entries[0].DefaultWorldWidth = 99f;
        Assert.Equal(before, SC2DecalPreviewDiff.EntriesSignature(d));
        d.Entries[0].Width = 65;
        Assert.NotEqual(before, SC2DecalPreviewDiff.EntriesSignature(d));
        d.Entries[0].Width = 64;
        d.AtlasWidth = 1024;
        Assert.NotEqual(before, SC2DecalPreviewDiff.EntriesSignature(d));
    }
}
