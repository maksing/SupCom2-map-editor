using System;
using System.IO;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The node reference page of the documentation lists every node of the catalog, so a new node cannot be added without describing it.</summary>
public class MissionNodeReferenceTests
{
    private static string FindReference()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Docs", "Systems", "MissionNodeReference.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void EveryNodeTypeIsListedInTheNodeReference()
    {
        var path = FindReference();
        // a checkout without the documentation folder has nothing to compare with
        if (path == null) return;
        var text = File.ReadAllText(path);

        var missing = SC2MissionNodeCatalog.All.Where(d => !text.Contains("`" + d.Type + "`")).Select(d => d.Type).ToList();

        Assert.True(missing.Count == 0, "Docs/Systems/MissionNodeReference.md does not list: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheNodeReferenceDoesNotListANodeThatDoesNotExist()
    {
        var path = FindReference();
        if (path == null) return;
        var types = SC2MissionNodeCatalog.All.Select(d => d.Type).ToHashSet();

        // the first backticked cell of every table row is a node type
        var listed = File.ReadAllLines(path)
            .Where(l => l.StartsWith("| ") && !l.StartsWith("| Node |") && !l.StartsWith("|---"))
            .Select(l => l.Split('|'))
            .Where(cells => cells.Length > 3)
            .Select(cells => cells[2].Trim().Trim('`'))
            .ToList();

        Assert.DoesNotContain(listed, t => !types.Contains(t));
        Assert.Equal(types.Count, listed.Distinct().Count());
    }
}
