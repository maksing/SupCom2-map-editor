namespace SC2CharacterizationTests;

public class EditorVersionTests
{
    [Fact]
    public void ParsesTagsWithAndWithoutTheV()
    {
        Assert.True(SC2EditorVersion.TryParse("v0.0.17-alpha", out var major, out var minor, out var patch, out var pre));
        Assert.Equal((0, 0, 17, "alpha"), (major, minor, patch, pre));
        Assert.True(SC2EditorVersion.TryParse("1.2", out major, out minor, out patch, out pre));
        Assert.Equal((1, 2, 0, ""), (major, minor, patch, pre));
        Assert.False(SC2EditorVersion.TryParse("latest", out _, out _, out _, out _));
        Assert.False(SC2EditorVersion.TryParse("", out _, out _, out _, out _));
        Assert.False(SC2EditorVersion.TryParse("1.x.3", out _, out _, out _, out _));
    }

    [Fact]
    public void ComparesNumbersNumerically()
    {
        Assert.True(SC2EditorVersion.IsNewer("v0.0.10-alpha", "0.0.9-alpha"));
        Assert.True(SC2EditorVersion.IsNewer("0.1.0-alpha", "0.0.99-alpha"));
        Assert.False(SC2EditorVersion.IsNewer("0.0.17-alpha", "0.0.17-alpha"));
        Assert.False(SC2EditorVersion.IsNewer("v0.0.16-alpha", "0.0.17-alpha"));
    }

    [Fact]
    public void AFinalVersionIsNewerThanItsPreReleases_AndPreReleasesAreOrderedByTheirParts()
    {
        Assert.True(SC2EditorVersion.IsNewer("1.0.0", "1.0.0-alpha"));
        Assert.False(SC2EditorVersion.IsNewer("1.0.0-alpha", "1.0.0"));
        Assert.True(SC2EditorVersion.IsNewer("1.0.0-beta", "1.0.0-alpha"));
        Assert.True(SC2EditorVersion.IsNewer("1.0.0-alpha.10", "1.0.0-alpha.9"));
        Assert.True(SC2EditorVersion.IsNewer("1.0.0-alpha.1", "1.0.0-alpha"));
    }

    [Fact]
    public void ATextThatIsNotAVersionIsNeverNewer()
    {
        Assert.False(SC2EditorVersion.IsNewer("nightly", "0.0.1"));
        Assert.False(SC2EditorVersion.IsNewer("", "0.0.1"));
    }

    [Fact]
    public void PicksTheHighestReleaseWhateverTheOrder_SkippingDraftsAndOddTags()
    {
        var json = """
        [
          {"tag_name":"v0.0.16-alpha","html_url":"https://github.com/o/r/releases/tag/v0.0.16-alpha","body":"older","draft":false,"prerelease":true},
          {"tag_name":"v0.0.18-alpha","html_url":"https://github.com/o/r/releases/tag/v0.0.18-alpha","body":"## New\n- thing","draft":false,"prerelease":true},
          {"tag_name":"v0.0.19-alpha","html_url":"https://github.com/o/r/releases/tag/v0.0.19-alpha","body":"unpublished","draft":true},
          {"tag_name":"nightly","html_url":"https://github.com/o/r/releases/tag/nightly","body":"x","draft":false},
          {"tag_name":"v0.0.9-alpha","html_url":"u","body":"","draft":false}
        ]
        """;
        var release = SC2UpdateFeed.PickLatestRelease(json);
        Assert.NotNull(release);
        Assert.Equal("v0.0.18-alpha", release.Tag);
        Assert.Equal("0.0.18-alpha", release.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v0.0.18-alpha", release.Url);
        Assert.Contains("thing", release.Notes);
    }

    [Fact]
    public void ARepositoryWithoutReleasesFallsBackToItsTags()
    {
        Assert.Null(SC2UpdateFeed.PickLatestRelease("[]"));
        var tags = SC2UpdateFeed.PickLatestTag("""[{"name":"v0.0.5-alpha"},{"name":"v0.0.17-alpha"},{"name":"v0.0.9-alpha"}]""");
        Assert.NotNull(tags);
        Assert.Equal("0.0.17-alpha", tags.Version);
        Assert.EndsWith("/releases/tag/v0.0.17-alpha", tags.Url);
    }

    [Fact]
    public void AnAnswerThatIsNotAListGivesNothing()
    {
        Assert.Null(SC2UpdateFeed.PickLatestRelease("""{"message":"Not Found","status":"404"}"""));
        Assert.Null(SC2UpdateFeed.PickLatestRelease("not json at all"));
        Assert.Null(SC2UpdateFeed.PickLatestRelease(null));
        Assert.Null(SC2UpdateFeed.PickLatestTag("<html></html>"));
    }

    [Fact]
    public void TheCurrentVersionOfTheEditorIsAVersion()
    {
        Assert.True(SC2EditorVersion.TryParse(SC2EditorVersion.Current, out _, out _, out _, out _));
    }
}
