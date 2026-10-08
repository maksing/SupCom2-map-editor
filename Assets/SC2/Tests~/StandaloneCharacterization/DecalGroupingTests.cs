using System.Linq;
using SC2.MapEditor.Document;
using Xunit;

namespace SC2CharacterizationTests;

public class DecalGroupingTests
{
    private static SC2DecalsDocument Document()
    {
        var doc = new SC2DecalsDocument();
        doc.Entries.Add(new SC2DecalAtlasEntry { Id = "e1", Name = "Scorch" });
        doc.Entries.Add(new SC2DecalAtlasEntry { Id = "e2", Name = "Crack" });
        doc.Placements.Add(new SC2DecalPlacement { Id = "p1", AtlasEntryId = "e1" });
        doc.Placements.Add(new SC2DecalPlacement { Id = "p2", AtlasEntryId = "e2" });
        doc.Placements.Add(new SC2DecalPlacement { Id = "p3", AtlasEntryId = "e1" });
        doc.Placements.Add(new SC2DecalPlacement { Id = "p4", AtlasEntryId = "gone" });
        return doc;
    }

    [Fact]
    public void PlacementsAreGroupedByTheirAtlasEntryAndSortedByName()
    {
        var groups = SC2DecalGrouping.ByType(Document());
        Assert.Equal(new[] { "(unknown)", "Crack", "Scorch" }, groups.Select(g => g.Name).ToArray());
        Assert.Equal(new[] { "p1", "p3" }, groups.Single(g => g.Name == "Scorch").PlacementIds.ToArray());
        Assert.Equal(new[] { "p2" }, groups.Single(g => g.Name == "Crack").PlacementIds.ToArray());
    }

    [Fact]
    public void IsolatingAGroupHidesEveryOtherPlacement()
    {
        var hidden = SC2DecalGrouping.IdsExcept(Document(), new[] { "p1", "p3" });
        Assert.Equal(new[] { "p2", "p4" }, hidden.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void AnEmptyDocumentHasNoGroups()
        => Assert.Empty(SC2DecalGrouping.ByType(new SC2DecalsDocument()));
}
