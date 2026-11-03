using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

/// <summary>
/// Static check of XAML bindings on dependency properties that WPF binds two-way by default (FrameworkPropertyMetadataOptions.BindsTwoWayByDefault).
/// Bound to a view-model property that has no public setter, such a binding throws InvalidOperationException when the page loads
/// (the Settings crash, Windows CI run 86). A binding passes when it says <c>Mode=OneWay</c> or <c>Mode=OneTime</c> (parsed exactly,
/// never by substring), or when it is two-way and its target is a property with a public setter on the view-model type. The type is found by
/// reflection on the Presentation assembly from <c>DataType</c>, nested <c>DataContext="{Binding X}"</c>, <c>ItemsSource</c> element types and a
/// per-view root type; where it cannot be found (RelativeSource, ElementName, templates, unknown roots) an explicit <c>Mode=TwoWay</c>
/// is required. <c>Mode=OneWayToSource</c> on these properties is always refused. Needs no WPF and runs on Linux.
/// </summary>
internal static partial class XamlBindingRules
{
    private static readonly string[] s_range = ["Value", "Minimum", "Maximum"];
    private static readonly string[] s_selector = ["SelectedItem", "SelectedValue", "SelectedIndex"];

    /// <summary>Element name to the properties that bind two-way by default on it (WPF 10 class hierarchy, by element name).</summary>
    private static readonly Dictionary<string, string[]> s_twoWayDefaults = BuildDefaults();

    private static Dictionary<string, string[]> BuildDefaults()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["TextBox"] = ["Text"],
            ["Run"] = ["Text"],
            ["ToggleButton"] = ["IsChecked"],
            ["CheckBox"] = ["IsChecked"],
            ["RadioButton"] = ["IsChecked"],
            ["MenuItem"] = ["IsChecked"],
            ["Expander"] = ["IsExpanded"],
            ["ComboBox"] = [.. s_selector, "Text"],
            ["DatePicker"] = ["SelectedDate", "DisplayDate", "Text"],
            ["Calendar"] = ["SelectedDate", "DisplayDate"],
            ["ListBoxItem"] = ["IsSelected"],
            ["ComboBoxItem"] = ["IsSelected"],
            ["ListViewItem"] = ["IsSelected"],
            ["TabItem"] = ["IsSelected"],
            ["TreeViewItem"] = ["IsSelected", "IsExpanded"],
        };
        foreach (var selector in new[] { "ListBox", "ListView", "TabControl", "Selector", "DataGrid" })
        {
            map[selector] = s_selector;
        }

        foreach (var range in new[] { "Slider", "ScrollBar", "ProgressBar", "RangeBase" })
        {
            map[range] = s_range;
        }

        return map;
    }

    /// <summary>
    /// Returns one message per binding that can throw at load. <paramref name="resolveType"/> maps a full CLR type name to a view-model type
    /// (or null); <paramref name="rootTypeOf"/> maps the root element's <c>x:Class</c> to the view-model type its DataContext holds.
    /// </summary>
    public static IEnumerable<string> Violations(XDocument doc, string file, Func<string, Type?> resolveType, Func<string, Type?> rootTypeOf)
    {
        var root = doc.Root;
        if (root is null)
        {
            yield break;
        }

        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var className = (string?)root.Attribute(xaml + "Class");
        var start = className is null ? null : rootTypeOf(className);
        var scopes = new Dictionary<XElement, Type?>();
        var results = new List<string>();
        Walk(root, start, scopes, resolveType, file, results);
        foreach (var result in results)
        {
            yield return result;
        }
    }

    private static void Walk(XElement element, Type? inherited, Dictionary<XElement, Type?> scopes, Func<string, Type?> resolveType, string file, List<string> results)
    {
        var name = element.Name.LocalName;
        var scope = ScopeOf(element, name, inherited, scopes, resolveType);
        var own = scope;
        var dataContext = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataContext" && a.Name.Namespace == XNamespace.None);
        if (dataContext is not null && TryParse(dataContext.Value, out var contextBinding))
        {
            own = contextBinding.IsResolvable ? ResolvePath(scope, contextBinding.Path, out _) : null;
        }

        scopes[element] = own;

        if (s_twoWayDefaults.TryGetValue(name, out var properties))
        {
            foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && properties.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            {
                Check(element, name, attribute.Name.LocalName, attribute.Value, own, results, file);
            }
        }

        if (name == "Setter" && element.Parent is { } style && style.Name.LocalName == "Style")
        {
            var target = StyleTarget(style);
            var property = (string?)element.Attribute("Property");
            var value = (string?)element.Attribute("Value");
            if (target is not null && property is not null && value is not null && s_twoWayDefaults.TryGetValue(target, out var styled) && styled.Contains(property, StringComparer.Ordinal))
            {
                Check(element, target, property, value, null, results, file);
            }
        }

        // <TextBox.Text><Binding Path="X" /></TextBox.Text>
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && element.Elements().ToArray() is [{ Name.LocalName: "Binding" } child] && s_twoWayDefaults.TryGetValue(name[..dot], out var viaElement) && viaElement.Contains(name[(dot + 1)..], StringComparer.Ordinal))
        {
            Check(child, name[..dot], name[(dot + 1)..], ElementToBindingText(child), own, results, file);
        }

        foreach (var child2 in element.Elements())
        {
            Walk(child2, own, scopes, resolveType, file, results);
        }
    }

    /// <summary>The DataContext type an element's children see before the element's own <c>DataContext</c> attribute applies.</summary>
    private static Type? ScopeOf(XElement element, string name, Type? inherited, Dictionary<XElement, Type?> scopes, Func<string, Type?> resolveType)
    {
        switch (name)
        {
            case "ControlTemplate" or "Style" or "ResourceDictionary" or "ItemsPanelTemplate":
                return null;
            case "DataTemplate" or "HierarchicalDataTemplate":
                var dataType = (string?)element.Attribute("DataType");
                if (dataType is not null)
                {
                    return TypeFromMarkup(element, dataType, resolveType);
                }

                if (element.Parent is { } holder && holder.Name.LocalName.EndsWith(".ItemTemplate", StringComparison.Ordinal) && holder.Parent is { } items
                    && scopes.TryGetValue(items, out var itemsScope)
                    && (string?)items.Attribute("ItemsSource") is { } source && TryParse(source, out var sourceBinding) && sourceBinding.IsResolvable)
                {
                    var collection = ResolvePath(itemsScope, sourceBinding.Path, out _);
                    return collection is null ? null : ElementTypeOf(collection);
                }

                return null;
            default:
                return inherited;
        }
    }

    private static string? StyleTarget(XElement style)
    {
        var target = (string?)style.Attribute("TargetType");
        if (target is null)
        {
            return null;
        }

        var match = XTypePattern().Match(target);
        return match.Success ? match.Groups["name"].Value : target.Trim();
    }

    private static Type? TypeFromMarkup(XElement context, string markup, Func<string, Type?> resolveType)
    {
        var match = XTypePattern().Match(markup);
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups["prefix"].Value;
        var ns = prefix.Length == 0 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(prefix);
        if (ns is null || !ns.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            return null;
        }

        var clr = ns.NamespaceName["clr-namespace:".Length..];
        var semicolon = clr.IndexOf(';', StringComparison.Ordinal);
        return resolveType($"{(semicolon < 0 ? clr : clr[..semicolon])}.{match.Groups["name"].Value}");
    }

    private static Type? ElementTypeOf(Type collection)
    {
        if (collection.IsArray)
        {
            return collection.GetElementType();
        }

        var candidates = collection.IsInterface ? collection.GetInterfaces().Append(collection) : collection.GetInterfaces();
        return candidates
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private static void Check(XElement element, string control, string property, string value, Type? scope, List<string> results, string file)
    {
        if (!TryParse(value, out var binding))
        {
            return; // A literal or another markup extension (StaticResource, DynamicResource, x:Static): not a binding.
        }

        var where = $"{file}:{((IXmlLineInfo)element).LineNumber} {control} {property}=\"{value}\"";
        var mode = binding.Mode;
        if (string.Equals(mode, "OneWay", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "OneTime", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(mode, "OneWayToSource", StringComparison.OrdinalIgnoreCase))
        {
            results.Add($"{where}: Mode=OneWayToSource is not allowed here; use OneWay, OneTime or a settable two-way target");
            return;
        }

        if (binding.IsResolvable && binding.Path.Length == 0)
        {
            results.Add($"{where}: a two-way binding needs a Path; add Mode=OneWay or Mode=OneTime");
            return;
        }

        if (binding.IsResolvable && scope is not null)
        {

            var target = ResolvePath(scope, binding.Path, out var failure);
            if (failure is not null)
            {
                results.Add($"{where}: {failure}");
                return;
            }

            if (target is not null)
            {
                return; // Resolved to a property with a public setter.
            }
        }

        if (!string.Equals(mode, "TwoWay", StringComparison.OrdinalIgnoreCase))
        {
            results.Add($"{where}: the binding target cannot be determined statically; say Mode=OneWay, Mode=OneTime or an explicit Mode=TwoWay");
        }
    }

    /// <summary>
    /// Walks <paramref name="path"/> from <paramref name="start"/>. Returns the type of the last property; <paramref name="failure"/> is set when a
    /// property is missing, or the last one has no public setter (the type is then still returned, only for a two-way check by the caller).
    /// For an intermediate step without a result the return is null with <paramref name="failure"/> null: the path was not resolvable.
    /// </summary>
    private static Type? ResolvePath(Type? start, string path, out string? failure)
    {
        failure = null;
        if (start is null)
        {
            return null;
        }

        if (path.Length == 0)
        {
            return start;
        }

        var current = start;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();
            if (segment.Length == 0 || segment.Contains('[', StringComparison.Ordinal) || segment.Contains('(', StringComparison.Ordinal))
            {
                return null;
            }

            var property = FindProperty(current, segment);
            if (property is null)
            {
                failure = $"{current.Name} has no public property {segment}";
                return null;
            }

            if (i == segments.Length - 1 && property.SetMethod is not { IsPublic: true })
            {
                failure = $"{current.Name}.{segment} has no public setter; bind it with Mode=OneWay or Mode=OneTime (WPF throws when the page loads)";
                return property.PropertyType;
            }

            current = property.PropertyType;
        }

        return current;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (var t = (Type?)type; t is not null; t = t.BaseType)
        {
            var found = t.GetProperties(Flags).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
            if (found is not null)
            {
                return found;
            }
        }

        return type.IsInterface ? type.GetInterfaces().SelectMany(i => i.GetProperties()).FirstOrDefault(p => p.Name == name) : null;
    }

    private static string ElementToBindingText(XElement binding)
    {
        var parts = binding.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}");
        return "{Binding " + string.Join(", ", parts) + "}";
    }

    internal readonly record struct ParsedBinding(string Path, string? Mode, bool IsResolvable);

    /// <summary>Parses <c>{Binding Path, Mode=X, ...}</c> exactly: arguments are split on top-level commas and Mode is the value of the Mode argument.</summary>
    internal static bool TryParse(string text, out ParsedBinding binding)
    {
        binding = default;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("{Binding", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
        {
            return false;
        }

        var inner = trimmed["{Binding".Length..^1];
        if (inner.Length > 0 && !char.IsWhiteSpace(inner[0]))
        {
            return false; // {BindingXyz ...}
        }

        var path = string.Empty;
        string? mode = null;
        var resolvable = true;
        foreach (var argument in SplitTopLevel(inner))
        {
            var text2 = argument.Trim();
            if (text2.Length == 0)
            {
                continue;
            }

            var equals = text2.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                path = text2;
                continue;
            }

            var key = text2[..equals].Trim();
            var value = text2[(equals + 1)..].Trim();
            switch (key)
            {
                case "Path":
                    path = value;
                    break;
                case "Mode":
                    mode = value;
                    break;
                case "RelativeSource" or "ElementName" or "Source":
                    resolvable = false;
                    break;
                default:
                    break;
            }
        }

        binding = new ParsedBinding(path, mode, resolvable);
        return true;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        yield return text[start..];
    }

    [GeneratedRegex(@"^\{\s*x:Type\s+(?:(?<prefix>[\w.]+):)?(?<name>[\w.]+)\s*\}$")]
    private static partial Regex XTypePattern();
}
