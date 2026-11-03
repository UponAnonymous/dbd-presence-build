// Covers: FR-CI-07 and local CI round 4 QA M-4 (R7: every field of the state file survives a change of any other field, the reviewer's scratch test D5 widened to all fields; a field added to the state
// later is covered without a new test, because the test fills every property it finds). docs/owner/free-ci.md.
using System.Reflection;
using System.Text.Json;

namespace DbdPresence.LocalCi.Tests;

public sealed class StateRoundTripTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly DateTimeOffset _at = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _folder.Dispose();

    /// <summary>Every property of the object set to a value that is not its default: texts, times, numbers, booleans, lists with one entry, nested records.</summary>
    private object Filled(Type type, int depth = 0)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            property.SetValue(instance, ValueFor(property.PropertyType, property.Name, depth));
        }

        return instance;
    }

    private object? ValueFor(Type type, string name, int depth)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;
        if (inner == typeof(string))
        {
            return $"{name} text";
        }

        if (inner == typeof(DateTimeOffset))
        {
            return _at.AddMinutes(name.Length);
        }

        if (inner == typeof(bool))
        {
            return true;
        }

        if (inner == typeof(int))
        {
            return name.Length + 3;
        }

        if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(inner)!;
            var element = inner.GetGenericArguments()[0];
            list.Add(element == typeof(string) ? $"{name} entry" : Filled(element, depth + 1));
            return list;
        }

        Assert.True(depth < 4, $"Cannot fill {name} of type {type}.");
        return Filled(inner, depth + 1);
    }

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private static string Json(object value) => JsonSerializer.Serialize(value, s_indented);

    private StateStore Store() => new(_folder.Paths.StateFile);

    [Fact]
    public void ThereIsNoPropertyOfTheStateThatTheFillerLeavesAtItsDefault()
    {
        var state = (CiState)Filled(typeof(CiState));

        foreach (var property in typeof(CiState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = property.GetValue(state);
            Assert.NotNull(value);
            Assert.False(value is System.Collections.IList { Count: 0 }, $"{property.Name} is an empty list");
        }
    }

    [Fact]
    public void EveryFieldOfTheState_SurvivesAChangeThatIsAboutNoneOfThem()
    {
        var store = Store();
        var full = (CiState)Filled(typeof(CiState));
        store.Update(s => Copy(full, s));
        var before = Json(store.Load());

        store.Update(_ => { });

        Assert.Equal(before, Json(store.Load()));
        Assert.Equal(Json(full), before);
    }

    [Theory]
    [InlineData(nameof(CiState.LastTokenCheck))]
    [InlineData(nameof(CiState.LastRunnerCheck))]
    [InlineData(nameof(CiState.LastWslUpdateNote))]
    [InlineData(nameof(CiState.KernelRelease))]
    [InlineData(nameof(CiState.WslVersionText))]
    [InlineData(nameof(CiState.BaseImageRecipe))]
    [InlineData(nameof(CiState.LastBootId))]
    [InlineData(nameof(CiState.RepositoryPrivate))]
    [InlineData(nameof(CiState.Probe))]
    public void ChangingOneField_LeavesEveryOtherFieldAsItWas(string changed)
    {
        var store = Store();
        var full = (CiState)Filled(typeof(CiState));
        store.Update(s => Copy(full, s));
        var other = (CiState)Filled(typeof(CiState));
        var property = typeof(CiState).GetProperty(changed)!;
        var replacement = changed == nameof(CiState.RepositoryPrivate) ? false : property.GetValue(other) is string text ? text + " (new)" : property.GetValue(other);

        store.Update(s => property.SetValue(s, replacement));

        var after = store.Load();
        foreach (var each in typeof(CiState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != changed))
        {
            Assert.Equal(Json(each.GetValue(full)!), Json(each.GetValue(after)!));
        }

        Assert.Equal(Json(replacement!), Json(property.GetValue(after)!));
    }

    [Fact]
    public void TheNamedFieldsOfTheRoundThreeMutants_SurviveAnotherChange()
    {
        var store = Store();
        store.Update(s =>
        {
            s.LastWslUpdateNote = "updated";
            s.KernelRelease = "5.15.167.4-microsoft-standard-WSL2";
            s.WslVersionText = "WSL version: 2.4.8.0";
            s.BaseImageRecipe = "abc";
            s.LastBootId = "6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab";
        });

        store.Update(s => s.LastTokenCheck = _at);

        var loaded = store.Load();
        Assert.Equal("updated", loaded.LastWslUpdateNote);
        Assert.Equal("5.15.167.4-microsoft-standard-WSL2", loaded.KernelRelease);
        Assert.Equal("WSL version: 2.4.8.0", loaded.WslVersionText);
        Assert.Equal("abc", loaded.BaseImageRecipe);
        Assert.Equal("6f2c0c1e-9a0b-4d6e-8c1a-0123456789ab", loaded.LastBootId);
    }

    /// <summary>The same writes with a redactor in the path (the program's own store always has one).</summary>
    [Fact]
    public void WithARedactor_EveryFieldStillSurvives_AndOnlyASecretIsReplaced()
    {
        var redactor = new Redactor();
        redactor.Register(new SecretText("THE-SECRET-VALUE-0123456789"));
        var store = new StateStore(_folder.Paths.StateFile, redactor);
        var full = (CiState)Filled(typeof(CiState));
        store.Update(s => Copy(full, s));
        full.ResidueItems[0].Text = "left THE-SECRET-VALUE-0123456789";

        store.Update(s => s.ResidueItems[0].Text = "left THE-SECRET-VALUE-0123456789");

        var loaded = store.Load();
        Assert.Equal("left [hidden]", loaded.ResidueItems[0].Text);
        Assert.Equal(full.ResidueItems[0].Kind, loaded.ResidueItems[0].Kind);
        Assert.Equal(full.ResidueItems[0].Subject, loaded.ResidueItems[0].Subject);
        foreach (var each in typeof(CiState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name is not (nameof(CiState.Residue) or nameof(CiState.ResidueItems))))
        {
            Assert.Equal(Json(each.GetValue(full)!), Json(each.GetValue(loaded)!));
        }
    }

    private static void Copy(CiState from, CiState to)
    {
        foreach (var property in typeof(CiState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            property.SetValue(to, property.GetValue(from));
        }
    }
}
