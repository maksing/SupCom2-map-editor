using System;
using System.Collections.Generic;
using SC2.MapEditor.Document;

/// <summary>
/// The version of this build of the editor, how versions compare, and where the newest one is published. A release is a git tag "v" + the
/// version ("v0.0.17-alpha"); <see cref="Current"/> has to be moved to the new version together with every release.
/// </summary>
public static class SC2EditorVersion
{
    public const string Current = "0.0.17-alpha";

    public const string Repository = "maksing/SupCom2-map-editor";
    public const string ReleasesPageUrl = "https://github.com/" + Repository + "/releases";
    public const string ReleasesApiUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";
    public const string TagsApiUrl = "https://api.github.com/repos/" + Repository + "/tags?per_page=30";

    /// <summary>Splits "v1.2.3-alpha.2" into its numbers and the pre-release part ("alpha.2", empty for a final version). The leading
    /// "v" is optional and a missing minor or patch number counts as 0.</summary>
    public static bool TryParse(string text, out int major, out int minor, out int patch, out string preRelease)
    {
        major = minor = patch = 0;
        preRelease = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] == 'v' || s[0] == 'V') s = s.Substring(1);
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = s.Substring(dash + 1);
            s = s.Substring(0, dash);
        }
        var parts = s.Split('.');
        if (parts.Length < 1 || parts.Length > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return false;
        major = numbers[0]; minor = numbers[1]; patch = numbers[2];
        return true;
    }

    /// <summary>Orders two versions like semantic versioning: by the numbers, then a final version after any of its pre-releases, then
    /// pre-releases by their dot-separated parts (numbers numerically and below words). A text that is not a version is below any version.</summary>
    public static int Compare(string a, string b)
    {
        var okA = TryParse(a, out var majorA, out var minorA, out var patchA, out var preA);
        var okB = TryParse(b, out var majorB, out var minorB, out var patchB, out var preB);
        if (!okA || !okB) return okA == okB ? 0 : okA ? 1 : -1;
        if (majorA != majorB) return majorA.CompareTo(majorB);
        if (minorA != minorB) return minorA.CompareTo(minorB);
        if (patchA != patchB) return patchA.CompareTo(patchB);
        return ComparePreRelease(preA, preB);
    }

    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out _, out _, out _, out _) && Compare(candidate, current) > 0;

    private static int ComparePreRelease(string a, string b)
    {
        var finalA = string.IsNullOrEmpty(a);
        var finalB = string.IsNullOrEmpty(b);
        if (finalA || finalB) return finalA == finalB ? 0 : finalA ? 1 : -1;
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var numA = int.TryParse(partsA[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var valueA);
            var numB = int.TryParse(partsB[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var valueB);
            int order;
            if (numA && numB) order = valueA.CompareTo(valueB);
            else if (numA != numB) order = numA ? -1 : 1;
            else order = string.CompareOrdinal(partsA[i], partsB[i]);
            if (order != 0) return order < 0 ? -1 : 1;
        }
        return partsA.Length.CompareTo(partsB.Length);
    }
}

/// <summary>A published version of the editor: its tag, the page to get it from and the release notes (may be empty).</summary>
public sealed class SC2ReleaseInfo
{
    public string Tag = "";
    public string Version = "";
    public string Url = SC2EditorVersion.ReleasesPageUrl;
    public string Notes = "";
}

/// <summary>Reads the answers of the GitHub API (see SC2EditorVersion.ReleasesApiUrl and TagsApiUrl) and picks the newest version.
/// Pure, so it can be tested; a text that is not the expected JSON gives null.</summary>
public static class SC2UpdateFeed
{
    /// <summary>The release with the highest version in a "list releases" answer. Drafts and tags that are not versions are skipped;
    /// pre-releases count (every version so far is an alpha).</summary>
    public static SC2ReleaseInfo PickLatestRelease(string json)
    {
        SC2ReleaseInfo best = null;
        foreach (var item in Items(json))
        {
            if (item.Get("draft").AsBool(false)) continue;
            var tag = item.Get("tag_name").AsString("");
            if (!SC2EditorVersion.TryParse(tag, out _, out _, out _, out _)) continue;
            if (best != null && SC2EditorVersion.Compare(tag, best.Tag) <= 0) continue;
            best = Make(tag, item.Get("html_url").AsString(""), item.Get("body").AsString(""));
        }
        return best;
    }

    /// <summary>The tag with the highest version in a "list tags" answer, for a repository that tags versions without publishing releases.</summary>
    public static SC2ReleaseInfo PickLatestTag(string json)
    {
        SC2ReleaseInfo best = null;
        foreach (var item in Items(json))
        {
            var tag = item.Get("name").AsString("");
            if (!SC2EditorVersion.TryParse(tag, out _, out _, out _, out _)) continue;
            if (best != null && SC2EditorVersion.Compare(tag, best.Tag) <= 0) continue;
            best = Make(tag, SC2EditorVersion.ReleasesPageUrl + "/tag/" + Uri.EscapeDataString(tag), "");
        }
        return best;
    }

    private static SC2ReleaseInfo Make(string tag, string url, string notes)
    {
        var version = tag.Trim();
        if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V')) version = version.Substring(1);
        return new SC2ReleaseInfo
        {
            Tag = tag,
            Version = version,
            Url = string.IsNullOrEmpty(url) ? SC2EditorVersion.ReleasesPageUrl : url,
            Notes = notes ?? "",
        };
    }

    private static IEnumerable<SC2JsonValue> Items(string json)
    {
        SC2JsonValue root;
        try { root = SC2JsonValue.Parse(json); }
        catch (Exception) { return new SC2JsonValue[0]; }
        return root == null ? new SC2JsonValue[0] : root.Items();
    }
}
