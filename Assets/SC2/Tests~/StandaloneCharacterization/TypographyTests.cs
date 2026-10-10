using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SC2CharacterizationTests;

public class TypographyTests
{
    private static string ScriptsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Scripts", "Unified", "SC2Type.cs");
            if (File.Exists(candidate)) return Path.Combine(directory.FullName, "Scripts");
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Scripts folder with SC2Type.cs not found above " + AppContext.BaseDirectory);
    }

    // The start window keeps its own sizes; SC2Type and SC2UiScale are where the numbers live.
    private static readonly string[] Exempt =
    {
        "SC2Type.cs", "SC2UiScale.cs", "SC2StartupUi.cs", "SC2GameViewEditorUI.Startup.cs",
    };

    [Fact]
    public void Font_sizes_come_from_the_SC2Type_roles_not_from_numbers_in_the_UI_code()
    {
        var offenders = new System.Collections.Generic.List<string>();
        var literal = new Regex(@"fontSize\s*=\s*\d+|SC2UiScale\.F\(\s*\d+\s*\)", RegexOptions.Compiled);
        foreach (var file in Directory.EnumerateFiles(ScriptsRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Exempt.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                var code = comment >= 0 ? line.Substring(0, comment) : line;
                if (literal.IsMatch(code)) offenders.Add($"{name}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0, "Use SC2Type.Title / Nav / Body / Caption / Micro instead of a number:\n" + string.Join("\n", offenders));
    }
}
