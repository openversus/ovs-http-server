using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenVersus.Server.Cli.Tests;

/// <summary>
/// Spectre's *Interpolated methods (MarkupLineInterpolated, MarkupInterpolated) escape every interpolated value, which is
/// what keeps a player's name from being read as markup. Markup inside an interpolated value is escaped too, and prints
/// as text ("[yellow]they were not online[/]"). Markup belongs in the format string. Read from the CLI's source, so it
/// holds for every command, including ones not written yet.
/// </summary>
public sealed partial class MarkupTests
{
    [GeneratedRegex(@"\[/?[a-z][a-z0-9 _#]*\]|\[/\]", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    private static string CliSource([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "OpenVersus.Server.Cli"));

    [Fact]
    public void NoInterpolatedValueCarriesMarkup()
    {
        var sources = Directory.EnumerateFiles(CliSource(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(sources);

        var found = new List<string>();
        foreach (string file in sources)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string method = call.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                    IdentifierNameSyntax name => name.Identifier.Text,
                    _ => "",
                };
                if (!method.EndsWith("Interpolated", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var hole in call.ArgumentList.Arguments.SelectMany(a => a.DescendantNodes().OfType<InterpolationSyntax>()))
                {
                    bool markup = hole.Expression.DescendantNodesAndSelf().Any(n =>
                        (n is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && Tag().IsMatch(literal.Token.ValueText))
                        || (n is InterpolatedStringTextSyntax text && Tag().IsMatch(text.TextToken.ValueText)));
                    if (markup)
                    {
                        found.Add($"{Path.GetFileName(file)}:{hole.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {hole}");
                    }
                }
            }
        }

        Assert.Empty(found);
    }
}
