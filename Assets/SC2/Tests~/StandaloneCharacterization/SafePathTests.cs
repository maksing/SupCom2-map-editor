using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class SafePathTests
{
    [Theory]
    [InlineData("terrain.win.bdf")]
    [InlineData("sub/dir/file.txt")]
    [InlineData("sub\\dir\\file.txt")]
    [InlineData("./file.txt")]
    public void NormalizeRelativeOrThrow_AcceptsOrdinaryRelativePaths(string input)
    {
        var normalized = SC2SafePath.NormalizeRelativeOrThrow(input);
        Assert.DoesNotContain("..", normalized.Split('/'));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("a/b/../../../escape.txt")]
    public void NormalizeRelativeOrThrow_RejectsParentTraversal(string input)
    {
        Assert.Throws<SC2UnsafePathException>(() => SC2SafePath.NormalizeRelativeOrThrow(input));
    }

    [Fact]
    public void NormalizeRelativeOrThrow_RejectsDrivePrefix()
    {
        Assert.Throws<SC2UnsafePathException>(
            () => SC2SafePath.NormalizeRelativeOrThrow("C:\\absolute\\path.txt"));
    }

    // Current (characterization) behavior, not necessarily desired behavior:
    // a single leading '/' is stripped by TrimStart('/') BEFORE the "//" UNC
    // check runs, so that check can never actually fire — a leading-slash or
    // even a "//server/share/..." path is silently reinterpreted as a plain
    // relative path rather than rejected. It's not a containment hole (
    // ResolveContainedOrThrow still re-checks the resolved path), but the
    // "//" guard in SC2SafePath.cs is dead code as written. Recorded here so
    // a future fix changes this test deliberately instead of by surprise.
    [Fact]
    public void NormalizeRelativeOrThrow_CurrentBehavior_LeadingSlashIsStrippedNotRejected()
    {
        var normalized = SC2SafePath.NormalizeRelativeOrThrow("/absolute/unix.txt");
        Assert.Equal("absolute/unix.txt", normalized);
    }

    [Fact]
    public void NormalizeRelativeOrThrow_CurrentBehavior_UncLookingPathIsStrippedNotRejected()
    {
        var normalized = SC2SafePath.NormalizeRelativeOrThrow("//server/share/file.txt");
        Assert.Equal("server/share/file.txt", normalized);
    }

    [Fact]
    public void ResolveContainedOrThrow_AllowsPathsInsideRoot()
    {
        var root = Directory.CreateTempSubdirectory("sc2safepath_");
        try
        {
            var resolved = SC2SafePath.ResolveContainedOrThrow(root.FullName, "sub/file.txt");
            Assert.StartsWith(root.FullName, resolved);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void ResolveContainedOrThrow_RejectsTraversalOutsideRoot()
    {
        var root = Directory.CreateTempSubdirectory("sc2safepath_");
        try
        {
            Assert.Throws<SC2UnsafePathException>(
                () => SC2SafePath.ResolveContainedOrThrow(root.FullName, "../../outside.txt"));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
