using System.IO;
using System.Security.Cryptography;
using System.Text;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class BlobStoreTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2blobstore_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void Put_ThenOpenRead_RoundTripsExactBytes()
    {
        var store = NewStore(out var root);
        try
        {
            var content = Encoding.UTF8.GetBytes("hello sc2 map document");
            SC2BlobReference reference;
            using (var ms = new MemoryStream(content))
                reference = store.Put(ms);

            Assert.False(reference.IsEmpty);
            Assert.Equal(content.Length, reference.Length);
            Assert.True(store.Contains(reference.ContentHash));

            using var read = store.OpenRead(reference.ContentHash);
            using var outMs = new MemoryStream();
            read.CopyTo(outMs);
            Assert.Equal(content, outMs.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Put_ComputesRealSha256OfContent()
    {
        var store = NewStore(out var root);
        try
        {
            var content = Encoding.UTF8.GetBytes("deterministic content");
            var expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            SC2BlobReference reference;
            using (var ms = new MemoryStream(content))
                reference = store.Put(ms);

            Assert.Equal(expectedHash, reference.ContentHash);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Put_SameContentTwice_DeduplicatesToOneFile()
    {
        var store = NewStore(out var root);
        try
        {
            var content = Encoding.UTF8.GetBytes("duplicate me");

            SC2BlobReference first;
            using (var ms = new MemoryStream(content))
                first = store.Put(ms);

            SC2BlobReference second;
            using (var ms = new MemoryStream(content))
                second = store.Put(ms);

            Assert.Equal(first.ContentHash, second.ContentHash);

            var blobFiles = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories);
            Assert.Single(blobFiles);

            var tempLeftovers = Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories);
            Assert.Empty(tempLeftovers);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Contains_ReturnsFalseForUnknownHash()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.False(store.Contains(new string('0', 64)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OpenRead_ThrowsForUnknownHash()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<FileNotFoundException>(() => store.OpenRead(new string('a', 64)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PathLayout_UsesFirstTwoHexCharsAsSubdirectory()
    {
        var store = NewStore(out var root);
        try
        {
            var content = Encoding.UTF8.GetBytes("layout check");
            SC2BlobReference reference;
            using (var ms = new MemoryStream(content))
                reference = store.Put(ms);

            var expectedPath = Path.Combine(root, reference.ContentHash.Substring(0, 2), reference.ContentHash + ".bin");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
