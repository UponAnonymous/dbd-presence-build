// Covers: NFR-REL (a page never crashes on load), UI_SPEC §5, wpf-ui-review checklist. Static check over the App's XAML (runs on Linux, needs no WPF).
// Every dependency property that WPF binds two-way by default (TextBox/Run/ComboBox Text, Range Value/Minimum/Maximum, ToggleButton IsChecked,
// Selector SelectedItem/SelectedValue/SelectedIndex, Expander IsExpanded, DatePicker ...) must be bound OneWay/OneTime, or two-way to a view-model
// property with a public setter (resolved by reflection on the built Presentation assembly), or, where the type cannot be found, say Mode=TwoWay.
// The Settings page crashed the app this way in Windows CI run 86 (UpdatesViewModel.Progress, a private setter behind a ProgressBar).
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

public sealed class XamlBindingRulesTests
{
    // Windows and views whose DataContext is assigned in code (MainWindow.xaml.cs) or documented as another view's model (StudioAdvancedView).
    private static readonly Dictionary<string, string> s_codeAssignedRoots = new(StringComparer.Ordinal)
    {
        ["DbdPresence.App.MainWindow"] = "DbdPresence.Presentation.ViewModels.ShellViewModel",
        ["DbdPresence.App.Views.FirstRunView"] = "DbdPresence.Presentation.ViewModels.FirstRunViewModel",
        ["DbdPresence.App.Views.UnlockView"] = "DbdPresence.Presentation.ViewModels.UnlockViewModel",
        ["DbdPresence.App.Views.StudioAdvancedView"] = "DbdPresence.Presentation.ViewModels.DiscordStatusViewModel",
    };

    [Fact]
    public void AppXaml_BindsTwoWayDefaultPropertiesOnlyToSettableOrExplicitTargets()
    {
        var appRoot = Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.App");
        var files = XamlFiles(appRoot);
        Assert.NotEmpty(files);

        var presentation = LoadPresentation();
        Type? Resolve(string fullName) => presentation.GetType(fullName, throwOnError: true);
        var roots = RootTypes(appRoot, Resolve);

        var violations = files
            .SelectMany(f => XamlBindingRules.Violations(XDocument.Load(f, LoadOptions.SetLineInfo), Path.GetRelativePath(appRoot, f), Resolve, roots))
            .ToArray();

        Assert.True(violations.Length == 0, "These bindings can throw when the page loads:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RootTypes_AreFoundForEveryPageAndWindow()
    {
        var appRoot = Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.App");
        var presentation = LoadPresentation();
        Type? Resolve(string fullName) => presentation.GetType(fullName, throwOnError: true);
        var roots = RootTypes(appRoot, Resolve);

        foreach (var page in new[] { "HomePage", "DiscordStatusPage", "CharactersPage", "AccountPage", "SettingsPage" })
        {
            Assert.True(roots($"DbdPresence.App.Views.{page}") is not null, page);
        }

        foreach (var code in s_codeAssignedRoots)
        {
            Assert.NotNull(Resolve(code.Value));
            Assert.Equal(Resolve(code.Value), roots(code.Key));
        }
    }

    private static string[] XamlFiles(string appRoot) =>
        [.. Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>The pages' models come from MainWindow's <c>DataTemplate DataType=... &lt;views:Page/&gt;</c> pairs; the rest from the code-assigned table.</summary>
    private static Func<string, Type?> RootTypes(string appRoot, Func<string, Type?> resolve)
    {
        var map = new Dictionary<string, Type?>(StringComparer.Ordinal);
        var window = XDocument.Load(Path.Combine(appRoot, "MainWindow.xaml"));
        foreach (var template in window.Descendants().Where(e => e.Name.LocalName == "DataTemplate" && e.Attribute("DataType") is not null))
        {
            var dataType = (string)template.Attribute("DataType")!;
            var page = template.Elements().Single();
            var model = ClrName(template, dataType.Trim('{', '}').Replace("x:Type", string.Empty, StringComparison.Ordinal).Trim());
            var pageName = $"{ClrNamespace(page.Name.NamespaceName)}.{page.Name.LocalName}";
            map[pageName] = model is null ? null : resolve(model);
        }

        foreach (var code in s_codeAssignedRoots)
        {
            map[code.Key] = resolve(code.Value);
        }

        return className => map.GetValueOrDefault(className);
    }

    private static string? ClrName(XElement context, string prefixed)
    {
        var parts = prefixed.Split(':', 2);
        var ns = parts.Length == 2 ? context.GetNamespaceOfPrefix(parts[0]) : null;
        return ns is null ? null : $"{ClrNamespace(ns.NamespaceName)}.{parts[1]}";
    }

    private static string ClrNamespace(string xmlNamespace)
    {
        var clr = xmlNamespace["clr-namespace:".Length..];
        var semicolon = clr.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? clr : clr[..semicolon];
    }

    private static Assembly LoadPresentation()
    {
        var path = Path.GetFullPath(ProductAssemblies.PathsOf("DbdPresence.Presentation")[0]);
        return new SiblingLoadContext(Path.GetDirectoryName(path)!).LoadFromAssemblyPath(path);
    }

    /// <summary>Loads the product assemblies from their output folder into a separate context; nothing from them is ever run.</summary>
    private sealed class SiblingLoadContext(string directory) : AssemblyLoadContext("presentation-inspection")
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    // ---- Self-tests of the rule, over a fake model (a mutant of the rule must fail one of these) ----

    private sealed class Row
    {
        public string Name { get; set; } = string.Empty;

        public string Locked { get; } = string.Empty;
    }

    private sealed class Model
    {
        public string Text { get; set; } = string.Empty;

        public bool Flag { get; set; }

        public double Level { get; set; }

        public double Progress { get; private set; }

        public string ReadOnly { get; } = string.Empty;

        public Row Selected { get; set; } = new();

        public Row Child { get; } = new();

        public IReadOnlyList<Row> Rows { get; } = [];
    }

    private const string Header = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:t=\"clr-namespace:Fake;assembly=Fake\"";

    private static string[] Run(string body)
    {
        var doc = XDocument.Parse($"<UserControl {Header} x:Class=\"Fake.View\">{body}</UserControl>", LoadOptions.SetLineInfo);
        return [.. XamlBindingRules.Violations(
            doc,
            "sample.xaml",
            name => name switch { "Fake.Model" => typeof(Model), "Fake.Row" => typeof(Row), _ => null },
            name => name == "Fake.View" ? typeof(Model) : null)];
    }

    [Theory]
    [InlineData("<TextBox Text=\"{Binding ReadOnly}\" />")]
    [InlineData("<TextBox Text=\"{Binding Path=ReadOnly, UpdateSourceTrigger=PropertyChanged}\" />")]
    [InlineData("<Run Text=\"{Binding ReadOnly}\" />")]
    [InlineData("<ProgressBar Value=\"{Binding Progress}\" />")]
    [InlineData("<ProgressBar Minimum=\"{Binding Progress}\" Maximum=\"{Binding Progress}\" />")]
    [InlineData("<Slider Value=\"{Binding Progress}\" />")]
    [InlineData("<ScrollBar Value=\"{Binding ReadOnly}\" />")]
    [InlineData("<CheckBox IsChecked=\"{Binding ReadOnly}\" />")]
    [InlineData("<RadioButton IsChecked=\"{Binding Progress}\" />")]
    [InlineData("<ToggleButton IsChecked=\"{Binding Progress}\" />")]
    [InlineData("<ComboBox SelectedItem=\"{Binding Rows}\" />")]
    [InlineData("<ComboBox SelectedValue=\"{Binding ReadOnly}\" />")]
    [InlineData("<ComboBox Text=\"{Binding ReadOnly}\" />")]
    [InlineData("<ListBox SelectedIndex=\"{Binding ReadOnly}\" />")]
    [InlineData("<Expander IsExpanded=\"{Binding Progress}\" />")]
    [InlineData("<TextBox Text=\"{Binding Child.Locked}\" />")]
    [InlineData("<TextBox Text=\"{Binding Missing}\" />")]
    [InlineData("<TextBox Text=\"{Binding}\" />")]
    [InlineData("<TextBox Text=\"{Binding ReadOnly, Mode=OneWayToSource}\" />")]
    [InlineData("<TextBox Text=\"{Binding Text, Mode=OneWayToSource}\" />")]
    [InlineData("<TextBox Text=\"{Binding ReadOnly, ConverterParameter=Mode=OneWay}\" />")]
    [InlineData("<TextBox Text=\"{Binding ReadOnly, Converter={StaticResource C}, ConverterParameter={x:Static Mode=OneTime}}\" />")]
    [InlineData("<TextBox Text=\"{Binding Text, RelativeSource={RelativeSource Self}}\" />")]
    [InlineData("<StackPanel DataContext=\"{Binding Child}\"><TextBox Text=\"{Binding Locked}\" /></StackPanel>")]
    [InlineData("<ItemsControl ItemsSource=\"{Binding Rows}\"><ItemsControl.ItemTemplate><DataTemplate><TextBox Text=\"{Binding Locked}\" /></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>")]
    [InlineData("<DataTemplate DataType=\"{x:Type t:Row}\"><TextBox Text=\"{Binding Locked}\" /></DataTemplate>")]
    [InlineData("<Style TargetType=\"{x:Type TextBox}\"><Setter Property=\"Text\" Value=\"{Binding Text}\" /></Style>")]
    [InlineData("<TextBox><TextBox.Text><Binding Path=\"ReadOnly\" /></TextBox.Text></TextBox>")]
    [InlineData("<ControlTemplate><TextBox Text=\"{Binding ReadOnly}\" /></ControlTemplate>")]
    public void Rule_FlagsATwoWayBindingThatCanThrow(string body)
    {
        Assert.NotEmpty(Run(body));
    }

    [Theory]
    [InlineData("<TextBox Text=\"{Binding Text}\" />")]
    [InlineData("<TextBox Text=\"{Binding Text, UpdateSourceTrigger=PropertyChanged}\" />")]
    [InlineData("<TextBox Text=\"{Binding ReadOnly, Mode=OneWay}\" />")]
    [InlineData("<TextBox Text=\"{Binding ReadOnly, Mode=OneTime}\" />")]
    [InlineData("<TextBox Text=\"{Binding Mode=OneWay}\" />")]
    [InlineData("<TextBox Text=\"Literal\" />")]
    [InlineData("<TextBox Text=\"{StaticResource Caption}\" />")]
    [InlineData("<TextBlock Text=\"{Binding ReadOnly}\" />")]
    [InlineData("<ProgressBar Value=\"{Binding Progress, Mode=OneWay}\" Minimum=\"0\" Maximum=\"100\" />")]
    [InlineData("<Slider Value=\"{Binding Level}\" />")]
    [InlineData("<CheckBox IsChecked=\"{Binding Flag}\" />")]
    [InlineData("<CheckBox IsChecked=\"{Binding Flag, Converter={StaticResource Not}, Mode=TwoWay}\" />")]
    [InlineData("<ComboBox SelectedItem=\"{Binding Selected}\" />")]
    [InlineData("<Expander IsExpanded=\"{Binding Flag, Mode=TwoWay}\" />")]
    [InlineData("<TextBox Text=\"{Binding Child.Name}\" />")]
    [InlineData("<StackPanel DataContext=\"{Binding Child}\"><TextBox Text=\"{Binding Name}\" /></StackPanel>")]
    [InlineData("<ItemsControl ItemsSource=\"{Binding Rows}\"><ItemsControl.ItemTemplate><DataTemplate><TextBox Text=\"{Binding Name}\" /></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>")]
    [InlineData("<DataTemplate DataType=\"{x:Type t:Row}\"><TextBox Text=\"{Binding Name}\" /></DataTemplate>")]
    [InlineData("<ControlTemplate><ToggleButton IsChecked=\"{Binding IsOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}\" /></ControlTemplate>")]
    [InlineData("<TextBox Text=\"{Binding Text, ElementName=Other, Mode=TwoWay}\" />")]
    public void Rule_AllowsSafeBindings(string body)
    {
        Assert.Empty(Run(body));
    }

    [Theory]
    [InlineData("<ControlTemplate><TextBox Text=\"{Binding Value}\" /></ControlTemplate>")]
    [InlineData("<TextBox Text=\"{Binding Value, RelativeSource={RelativeSource AncestorType=UserControl}}\" />")]
    [InlineData("<DataTemplate><TextBox Text=\"{Binding Value}\" /></DataTemplate>")]
    public void Rule_RequiresAnExplicitModeWhereTheTypeCannotBeFound(string body)
    {
        var violations = Run(body);

        Assert.Single(violations);
        Assert.Contains("explicit Mode=TwoWay", violations[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_ReportsTheFileLineAndBinding()
    {
        var violations = Run("\n<TextBox Text=\"{Binding ReadOnly}\" />");

        var single = Assert.Single(violations);
        Assert.StartsWith("sample.xaml:2 TextBox Text=\"{Binding ReadOnly}\"", single, StringComparison.Ordinal);
        Assert.Contains("no public setter", single, StringComparison.Ordinal);
    }
}
