using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class TerrainHeightsCommandTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2terrainheightsblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void Execute_ReplacesHeightsAndMinMax()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetTerrainHeightsCommand(new byte[] { 1, 2, 3, 4 }, 10f, 200f, store));

            var grid = document.Terrain.Grid;
            Assert.Equal(10f, grid.MinimumHeight);
            Assert.Equal(200f, grid.MaximumHeight);
            using var stream = store.OpenRead(grid.HeightsFloat32.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Undo_RestoresPreviousHeightsAndMinMax()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            document.Terrain.Grid.HeightsFloat32 = store.Put(new MemoryStream(new byte[] { 9, 9 }));
            document.Terrain.Grid.MinimumHeight = 5f;
            document.Terrain.Grid.MaximumHeight = 50f;
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetTerrainHeightsCommand(new byte[] { 1, 1 }, 10f, 200f, store));
            history.Undo(document);

            var grid = document.Terrain.Grid;
            Assert.Equal(5f, grid.MinimumHeight);
            Assert.Equal(50f, grid.MaximumHeight);
            using var stream = store.OpenRead(grid.HeightsFloat32.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 9, 9 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Do_CascadesToHfieldCostsAndCollision2()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var command = new SC2SetTerrainHeightsCommand(new byte[] { 1 }, 0f, 1f, store);

            var changeSet = command.Do(document);

            Assert.Equal(SC2DirtyFlags.TerrainGeometry, changeSet.DirectDirtyFlags);
            Assert.Contains(SC2DomainId.Terrain, changeSet.ChangedDomains);
            Assert.Equal(new[] { SC2DomainId.Hfield, SC2DomainId.Costs, SC2DomainId.Collision2 }, changeSet.InvalidatedDomains);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => new SC2SetTerrainHeightsCommand(null, 0f, 1f, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetTerrainHeightsCommand(new byte[] { 1 }, 0f, 1f, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class MaterialTextureCommandTests
{
    private static SC2MapDocument DocumentWithMaterial(string id)
    {
        var document = new SC2MapDocument();
        document.Terrain.Materials.Add(new SC2MaterialDocument { Id = id, Name = "Mat" });
        return document;
    }

    [Theory]
    [InlineData(SC2MaterialTextureSlot.Diffuse)]
    [InlineData(SC2MaterialTextureSlot.Normal)]
    [InlineData(SC2MaterialTextureSlot.Mask)]
    public void Execute_SetsTheTargetedSlotOnly(SC2MaterialTextureSlot slot)
    {
        var document = DocumentWithMaterial("mat-1");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialTextureCommand("mat-1", slot, "grass_diffuse"));

        var material = document.Terrain.FindMaterial("mat-1");
        var (d, n, m) = (material.Diffuse.Name, material.Normal.Name, material.Mask.Name);
        Assert.Equal(slot == SC2MaterialTextureSlot.Diffuse ? "grass_diffuse" : "", d);
        Assert.Equal(slot == SC2MaterialTextureSlot.Normal ? "grass_diffuse" : "", n);
        Assert.Equal(slot == SC2MaterialTextureSlot.Mask ? "grass_diffuse" : "", m);
    }

    [Fact]
    public void Undo_RestoresThePreviousTextureName()
    {
        var document = DocumentWithMaterial("mat-1");
        document.Terrain.FindMaterial("mat-1").Diffuse.Name = "original_diffuse";
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialTextureCommand("mat-1", SC2MaterialTextureSlot.Diffuse, "new_diffuse"));
        history.Undo(document);

        Assert.Equal("original_diffuse", document.Terrain.FindMaterial("mat-1").Diffuse.Name);
    }

    [Fact]
    public void Do_ThrowsWhenMaterialIdIsNotFound()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetMaterialTextureCommand("missing", SC2MaterialTextureSlot.Diffuse, "x");

        Assert.Throws<InvalidOperationException>(() => command.Do(document));
    }

    [Fact]
    public void Do_ReturnsChangeSetWithTerrainMaterialsDirtyFlag()
    {
        var document = DocumentWithMaterial("mat-1");
        var command = new SC2SetMaterialTextureCommand("mat-1", SC2MaterialTextureSlot.Normal, "n");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.TerrainMaterials, changeSet.DirectDirtyFlags);
        Assert.Empty(changeSet.InvalidatedDomains);
    }

    [Fact]
    public void UndoRedo_PreservesStableTextureIdentityAndPaths()
    {
        var document = DocumentWithMaterial("mat-1");
        var material = document.Terrain.FindMaterial("mat-1");
        material.Diffuse = new SC2TextureReference { Id = "old", Name = "old", ResourcePath = "old/path", Source = "Vanilla" };
        var history = new SC2CommandHistory();
        var next = new SC2TextureReference
        {
            Id = "custom:abc",
            Name = "cliff",
            GamePath = @"C:\Textures\cliff.dds",
            Source = "Custom",
            ContentHash = "abc",
        };

        history.Execute(document, new SC2SetMaterialTextureCommand("mat-1", SC2MaterialTextureSlot.Diffuse, next));
        Assert.Equal("custom:abc", material.Diffuse.Id);
        Assert.Equal(@"C:\Textures\cliff.dds", material.Diffuse.GamePath);
        history.Undo(document);
        Assert.Equal("old", material.Diffuse.Id);
        Assert.Equal("old/path", material.Diffuse.ResourcePath);
        history.Redo(document);
        Assert.Equal("abc", material.Diffuse.ContentHash);
    }
}

public class MaterialPairLinkCommandTests
{
    private static SC2MapDocument DocumentWithPair(string pairId, string mat1, string mat2)
    {
        var document = new SC2MapDocument();
        document.Terrain.MaterialPairs.Add(new SC2MaterialPairDocument { Id = pairId, Material1Id = mat1, Material2Id = mat2 });
        return document;
    }

    [Fact]
    public void Execute_RelinksBothMaterialIds()
    {
        var document = DocumentWithPair("pair-1", "mat-a", "mat-b");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairLinkCommand("pair-1", "mat-c", "mat-d"));

        var pair = document.Terrain.FindPair("pair-1");
        Assert.Equal("mat-c", pair.Material1Id);
        Assert.Equal("mat-d", pair.Material2Id);
    }

    [Fact]
    public void Undo_RestoresThePreviousLinks()
    {
        var document = DocumentWithPair("pair-1", "mat-a", "mat-b");
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairLinkCommand("pair-1", "mat-c", "mat-d"));
        history.Undo(document);

        var pair = document.Terrain.FindPair("pair-1");
        Assert.Equal("mat-a", pair.Material1Id);
        Assert.Equal("mat-b", pair.Material2Id);
    }

    [Fact]
    public void Do_ThrowsWhenPairIdIsNotFound()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetMaterialPairLinkCommand("missing", "a", "b");

        Assert.Throws<InvalidOperationException>(() => command.Do(document));
    }

    [Fact]
    public void RelinkToMissingMaterial_IsCaughtBySC2TerrainValidatorAfterward()
    {
        var document = DocumentWithPair("pair-1", "mat-a", "mat-b");
        document.Terrain.Materials.Add(new SC2MaterialDocument { Id = "mat-a" });
        document.Terrain.Materials.Add(new SC2MaterialDocument { Id = "mat-b" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMaterialPairLinkCommand("pair-1", "mat-a", "mat-does-not-exist"));
        var report = SC2TerrainValidator.Validate(document.Terrain);

        Assert.Contains(report.Messages, m => m.Code == "TER003");
    }
}

public class TrianglePairAssignmentsCommandTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2paintblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void Execute_ReplacesBlobAndSlotIds()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetTrianglePairAssignmentsCommand(new byte[] { 0, 0, 1, 0 }, new[] { "pair-1" }, store));

            var map = document.Terrain.PairAssignments;
            Assert.Equal(new List<string> { "pair-1" }, map.SlotIds);
            using var stream = store.OpenRead(map.SlotIndexPerTriangleInt16.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Undo_RestoresPreviousBlobAndSlotIds()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            document.Terrain.PairAssignments.SlotIndexPerTriangleInt16 = store.Put(new MemoryStream(new byte[] { 9 }));
            document.Terrain.PairAssignments.SlotIds.Add("pair-original");
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetTrianglePairAssignmentsCommand(new byte[] { 1 }, new[] { "pair-new" }, store));
            history.Undo(document);

            var map = document.Terrain.PairAssignments;
            Assert.Equal(new List<string> { "pair-original" }, map.SlotIds);
            using var stream = store.OpenRead(map.SlotIndexPerTriangleInt16.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 9 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => new SC2SetTrianglePairAssignmentsCommand(null, new[] { "a" }, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetTrianglePairAssignmentsCommand(new byte[] { 1 }, null, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetTrianglePairAssignmentsCommand(new byte[] { 1 }, new[] { "a" }, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Do_ReturnsChangeSetWithTerrainMaterialsDirtyFlag()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var command = new SC2SetTrianglePairAssignmentsCommand(new byte[] { 1 }, new[] { "a" }, store);

            var changeSet = command.Do(document);

            Assert.Equal(SC2DirtyFlags.TerrainMaterials, changeSet.DirectDirtyFlags);
            Assert.Empty(changeSet.InvalidatedDomains);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
