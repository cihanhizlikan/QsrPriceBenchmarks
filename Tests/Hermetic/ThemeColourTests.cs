using System.Text.RegularExpressions;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// <c>Ui/Themes/Dark.xaml</c> is the one place the window's colours are defined: every other XAML file and every
/// <c>.cs</c> file under <c>Ui/</c> reads them as resources. A colour written anywhere else is a second theme
/// nobody maintains, and was found only by a sweep reading the tree until this guard existed (adopted from Yalbuz's
/// <c>StyleFoundationTests</c>, 2026-11-07). This asserts WHERE a colour may be defined, never which colour it is.
/// <c>Transparent</c> is the absence of a colour and is allowed everywhere.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class ThemeColourTests
{
    /// <summary>
    /// Out of scope by settled decision (2026-10-25, "the initials-badge palette stays in code"): the fallback badge
    /// colours and their text colours are chosen per chain in code. Re-opened by a second theme.
    /// </summary>
    private static readonly string[] Exempt = ["Themes/Dark.xaml", "ViewModels/ChainViewModel.cs"];

    // A colour-valued attribute (or a Setter of a colour-valued property) whose value is a literal rather than a
    // markup extension; Transparent is the one literal allowed.
    [GeneratedRegex("""(?:\b(?:Background|Foreground|Fill|Stroke|BorderBrush|Color|CaretBrush|SelectionBrush|OpacityMask)="|Property="(?:Background|Foreground|Fill|Stroke|BorderBrush|CaretBrush|SelectionBrush)"\s+Value=")(?!\{)(?!Transparent")[^"]+""")]
    private static partial Regex XamlColourLiteral();

    [GeneratedRegex("""\bColor\.From(?:Rgb|Argb|ScRgb)\b|\bColors\.(?!Transparent\b)[A-Z]\w*|\bBrushes\.(?!Transparent\b)[A-Z]\w*|\bColorConverter\.ConvertFromString\b""")]
    private static partial Regex CodeColourLiteral();

    [Fact(DisplayName = "No colour is defined in the UI outside the theme dictionary")]
    public void Colours_AreDefinedOnlyInTheTheme()
    {
        string ui = Path.Combine(RepositoryRoot(), "Ui");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(ui, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(ui, file).Replace('\\', '/');
            bool isXaml = relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            bool isCode = relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if ((!isXaml && !isCode)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal)
                || Exempt.Contains(relative))
            {
                continue;
            }

            Regex pattern = isXaml ? XamlColourLiteral() : CodeColourLiteral();
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pattern.Matches(lines[i]))
                {
                    offenders.Add($"Ui/{relative}:{i + 1}: {match.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A colour is defined outside Ui/Themes/Dark.xaml; add it to the theme and reference the resource:\n"
            + string.Join("\n", offenders));
    }

    [Fact(DisplayName = "The theme guard scans the real UI folder")]
    public void Guard_FindsTheThemeItProtects()
    {
        // A guard that scanned an empty or wrong folder would pass forever; this proves it is looking at the tree.
        string theme = Path.Combine(RepositoryRoot(), "Ui", "Themes", "Dark.xaml");
        Assert.True(File.Exists(theme), $"Expected the theme dictionary at {theme}.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QsrPriceBenchmarks.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
