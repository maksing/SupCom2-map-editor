using System;
using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class CostsCommandTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2costscmdblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static SC2MapDocument DocumentWithLayer(ISC2BlobStore store, string layerId, byte[] initialGrid)
    {
        var document = new SC2MapDocument();
        document.Costs.Layers.Add(new SC2CostsLayerDocument
        {
            Id = layerId,
            FieldName = layerId,
            DisplayName = layerId,
            FinalGrid = store.Put(new MemoryStream(initialGrid)),
        });
        return document;
    }

    [Fact]
    public void Execute_ReplacesTheLayersFinalGridBytes()
    {
        var store = NewStore(out var root);
        try
        {
            var document = DocumentWithLayer(store, "Land_Small", new byte[] { 1, 1, 1 });
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetCostsLayerGridCommand("Land_Small", new byte[] { 255, 5, 1 }, store));

            var layer = document.Costs.Find("Land_Small");
            using var stream = store.OpenRead(layer.FinalGrid.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 255, 5, 1 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Undo_RestoresThePreviousGridReferenceWithoutRewritingTheStore()
    {
        var store = NewStore(out var root);
        try
        {
            var document = DocumentWithLayer(store, "Land_Small", new byte[] { 1, 1, 1 });
            var originalRef = document.Costs.Find("Land_Small").FinalGrid;
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetCostsLayerGridCommand("Land_Small", new byte[] { 255, 5, 1 }, store));
            history.Undo(document);

            var layer = document.Costs.Find("Land_Small");
            Assert.Equal(originalRef.ContentHash, layer.FinalGrid.ContentHash);
            using var stream = store.OpenRead(layer.FinalGrid.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 1, 1, 1 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Redo_ReappliesTheNewGrid()
    {
        var store = NewStore(out var root);
        try
        {
            var document = DocumentWithLayer(store, "Land_Small", new byte[] { 1, 1, 1 });
            var history = new SC2CommandHistory();

            history.Execute(document, new SC2SetCostsLayerGridCommand("Land_Small", new byte[] { 255, 5, 1 }, store));
            history.Undo(document);
            history.Redo(document);

            var layer = document.Costs.Find("Land_Small");
            using var stream = store.OpenRead(layer.FinalGrid.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 255, 5, 1 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Do_ReturnsChangeSetWithCostsDirtyFlagAndDomain()
    {
        var store = NewStore(out var root);
        try
        {
            var document = DocumentWithLayer(store, "Land_Small", new byte[] { 1 });
            var command = new SC2SetCostsLayerGridCommand("Land_Small", new byte[] { 255 }, store);

            var changeSet = command.Do(document);

            Assert.Equal(SC2DirtyFlags.Costs, changeSet.DirectDirtyFlags);
            Assert.Contains(SC2DomainId.Costs, changeSet.ChangedDomains);
            Assert.Empty(changeSet.InvalidatedDomains);
            Assert.Equal("Paint Costs.Land_Small", changeSet.CommandName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Do_ThrowsWhenLayerIdIsNotFound()
    {
        var store = NewStore(out var root);
        try
        {
            var document = new SC2MapDocument();
            var command = new SC2SetCostsLayerGridCommand("DoesNotExist", new byte[] { 1 }, store);

            Assert.Throws<InvalidOperationException>(() => command.Do(document));
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
            Assert.Throws<ArgumentNullException>(() => new SC2SetCostsLayerGridCommand(null, new byte[] { 1 }, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetCostsLayerGridCommand("Land_Small", null, store));
            Assert.Throws<ArgumentNullException>(() => new SC2SetCostsLayerGridCommand("Land_Small", new byte[] { 1 }, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class CostsValidatorTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2costsvalidateblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void Validate_ErrorsOnDuplicateLayerIds()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = new SC2CostsDocument();
            doc.Layers.Add(new SC2CostsLayerDocument { Id = "Land_Small", FinalGrid = store.Put(new MemoryStream(new byte[] { 1 })) });
            doc.Layers.Add(new SC2CostsLayerDocument { Id = "Land_Small", FinalGrid = store.Put(new MemoryStream(new byte[] { 1 })) });

            var report = SC2CostsValidator.Validate(doc);

            Assert.Contains(report.Messages, m => m.Code == "COST001" && m.Severity == SC2ValidationSeverity.Error);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Validate_ErrorsOnEmptyFinalGrid()
    {
        var doc = new SC2CostsDocument();
        doc.Layers.Add(new SC2CostsLayerDocument { Id = "Land_Small", FinalGrid = SC2BlobReference.Empty });

        var report = SC2CostsValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "COST002" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_NoErrorsForWellFormedDocument()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = new SC2CostsDocument();
            doc.Layers.Add(new SC2CostsLayerDocument { Id = "Land_Small", FinalGrid = store.Put(new MemoryStream(new byte[] { 1 })) });
            doc.Layers.Add(new SC2CostsLayerDocument { Id = "Land_Medium", FinalGrid = store.Put(new MemoryStream(new byte[] { 1 })) });

            var report = SC2CostsValidator.Validate(doc);

            Assert.Empty(report.Messages);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2CostsValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
