// Covers: NFR-ACC (every spoken name sits on an element UI Automation exposes), UI_SPEC §7, wpf-ui-review checklist.
// Static check over the App's XAML (no WPF needed, so it runs on Linux): StackPanel, Border, Grid, DockPanel, WrapPanel and
// UniformGrid have no automation peer, so an AutomationProperties.Name or AutomationId on them is never seen by a screen reader
// or a UI test. Put the name on the focusable control or on a TextBlock instead. A deliberate exception needs an XML comment
// directly before the element that starts with "automation-exception:" and gives the reason.
using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

public sealed class XamlAutomationRulesTests
{
    private const string ExceptionMarker = "automation-exception:";

    private static readonly HashSet<string> s_peerless = new(StringComparer.Ordinal)
    {
        "StackPanel", "Border", "Grid", "DockPanel", "WrapPanel", "UniformGrid",
    };

    private static readonly string[] s_automationAttributes = ["AutomationProperties.Name", "AutomationProperties.AutomationId"];

    [Fact]
    public void AppXaml_NeverNamesAnElementWithoutAnAutomationPeer()
    {
        var appRoot = Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.App");
        var files = Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files.SelectMany(f => Violations(XDocument.Load(f, LoadOptions.SetLineInfo), Path.GetRelativePath(appRoot, f))).ToArray();

        Assert.True(violations.Length == 0, "Move these names onto a focusable control or a TextBlock:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Rule_FlagsANamedPanel()
    {
        var doc = XDocument.Parse("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><StackPanel AutomationProperties.Name="x" /><Border AutomationProperties.AutomationId="y" /></Grid>""", LoadOptions.SetLineInfo);

        Assert.Equal(2, Violations(doc, "sample.xaml").Count());
    }

    [Fact]
    public void Rule_AllowsControlsTextBlocksUserControlsAndCommentedExceptions()
    {
        var doc = XDocument.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Button AutomationProperties.Name="ok" />
              <TextBlock AutomationProperties.AutomationId="ok" />
              <UserControl AutomationProperties.AutomationId="ok" />
              <!-- automation-exception: wrapper exposed through a custom peer -->
              <Border AutomationProperties.Name="ok" />
              <StackPanel />
            </Grid>
            """,
            LoadOptions.SetLineInfo);

        Assert.Empty(Violations(doc, "sample.xaml"));
    }

    [Fact]
    public void Rule_RequiresTheCommentToSitDirectlyBeforeTheElement()
    {
        var doc = XDocument.Parse(
            """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <!-- automation-exception: applies to the next element only -->
              <Border AutomationProperties.Name="ok" />
              <Border AutomationProperties.Name="not ok" />
            </Grid>
            """,
            LoadOptions.SetLineInfo);

        Assert.Single(Violations(doc, "sample.xaml"));
    }

    private static IEnumerable<string> Violations(XDocument doc, string file)
    {
        foreach (var element in doc.Descendants().Where(e => s_peerless.Contains(e.Name.LocalName)))
        {
            var attribute = element.Attributes().FirstOrDefault(a => s_automationAttributes.Contains(a.Name.LocalName, StringComparer.Ordinal));
            if (attribute is null || HasException(element))
            {
                continue;
            }

            var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
            yield return $"{file}:{line}: <{element.Name.LocalName}> sets {attribute.Name.LocalName}";
        }
    }

    private static bool HasException(XElement element) =>
        element.PreviousNode is XComment comment && comment.Value.TrimStart().StartsWith(ExceptionMarker, StringComparison.Ordinal);
}
