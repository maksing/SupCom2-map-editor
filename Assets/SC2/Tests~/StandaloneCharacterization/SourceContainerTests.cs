using System.IO;
using System.IO.Compression;
using System.Text;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class SourceContainerTests
{
    [Fact]
    public void DirectoryReader_EnumeratesNestedFilesWithForwardSlashPaths()
    {
        var root = Directory.CreateTempSubdirectory("sc2sourcedir_").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "top.txt"), "top");
            File.WriteAllText(Path.Combine(root, "sub", "nested.txt"), "nested content");

            using var reader = new SC2DirectorySourceContainerReader(root);

            Assert.Equal(SC2SourceContainerKind.Directory, reader.Kind);
            Assert.Equal(2, reader.Entries.Count);

            var nested = Assert.Single(reader.Entries, e => e.NormalizedPath == "sub/nested.txt");
            Assert.Equal(new FileInfo(Path.Combine(root, "sub", "nested.txt")).Length, nested.UncompressedLength);

            using var stream = reader.OpenEntry("sub/nested.txt");
            using var streamReader = new StreamReader(stream);
            Assert.Equal("nested content", streamReader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DirectoryReader_OpenEntry_RejectsTraversalAttempt()
    {
        var root = Directory.CreateTempSubdirectory("sc2sourcedir_").FullName;
        try
        {
            using var reader = new SC2DirectorySourceContainerReader(root);
            Assert.Throws<SC2UnsafePathException>(() => reader.OpenEntry("../outside.txt"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ZipReader_EnumeratesEntriesAndReadsContent()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"sc2sourcezip_{System.Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("scenario.lua");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("-- scenario contents --");
            }

            using var reader = new SC2ZipSourceContainerReader(zipPath);
            Assert.Equal(SC2SourceContainerKind.ZipArchive, reader.Kind);
            var single = Assert.Single(reader.Entries);
            Assert.Equal("scenario.lua", single.NormalizedPath);

            using var stream = reader.OpenEntry("scenario.lua");
            using var streamReader = new StreamReader(stream);
            Assert.Equal("-- scenario contents --", streamReader.ReadToEnd());
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    // Characterization of the zip-slip fix (audit finding P1.7): a zip entry
    // whose stored name contains a parent-directory segment must make the
    // WHOLE archive fail to open, not silently extract outside the target
    // directory later.
    [Fact]
    public void ZipReader_RejectsWholeArchiveWhenAnyEntryNameTraversesUp()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"sc2sourcezip_{System.Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var goodEntry = archive.CreateEntry("scenario.lua");
                using (var writer = new StreamWriter(goodEntry.Open()))
                    writer.Write("fine");

                var evilEntry = archive.CreateEntry("../../evil.txt");
                using (var writer = new StreamWriter(evilEntry.Open()))
                    writer.Write("zip-slip payload");
            }

            Assert.Throws<SC2UnsafePathException>(() => new SC2ZipSourceContainerReader(zipPath));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void Factory_PicksDirectoryReaderForDirectoryPath()
    {
        var root = Directory.CreateTempSubdirectory("sc2sourcefactory_").FullName;
        try
        {
            var factory = new SC2SourceContainerReaderFactory();
            using var reader = factory.Open(root);
            Assert.IsType<SC2DirectorySourceContainerReader>(reader);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Factory_PicksZipReaderForFilePath()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"sc2sourcefactory_{System.Guid.NewGuid():N}.zip");
        try
        {
            using (ZipFile.Open(zipPath, ZipArchiveMode.Create)) { }

            var factory = new SC2SourceContainerReaderFactory();
            using var reader = factory.Open(zipPath);
            Assert.IsType<SC2ZipSourceContainerReader>(reader);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void Factory_ThrowsForMissingPath()
    {
        var factory = new SC2SourceContainerReaderFactory();
        var missingPath = Path.Combine(Path.GetTempPath(), $"sc2missing_{System.Guid.NewGuid():N}");
        Assert.Throws<FileNotFoundException>(() => factory.Open(missingPath));
    }
}
