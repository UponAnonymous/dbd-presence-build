// Covers: D35 (the folded name the app reports; the identical folded name is how the service counts matches), PLAN_V2_S5B §2.2 check 4 (the fold
// is the same in the app and the service: the shared fixture tests/fixtures/roster/fold-cases.json is checked by service/test/roster.test.ts too).
using System.Text.Json;
using DbdPresence.Core.Roster;

namespace DbdPresence.Core.Tests.Roster;

public sealed class RosterNameFoldTests
{
    public static TheoryData<string, string?> Cases
    {
        get
        {
            var data = new TheoryData<string, string?>();
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "roster", "fold-cases.json")));
            foreach (var c in json.RootElement.GetProperty("cases").EnumerateArray())
            {
                data.Add(c.GetProperty("input").GetString()!, c.GetProperty("fold").ValueKind == JsonValueKind.Null ? null : c.GetProperty("fold").GetString());
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fold_MatchesTheSharedCases(string input, string? expected)
    {
        Assert.Equal(expected, RosterNameFold.Fold(input));
    }

    [Fact]
    public void SharedCases_AreEnough()
    {
        Assert.True(Cases.Count >= 20);
    }

    [Theory]
    [InlineData("The Lantern", "the lantern")]
    [InlineData("X", null)]
    [InlineData("", null)]
    [InlineData("東京", null)]
    [InlineData("abcdefghij abcdefghij abcdefghij abcdefghij", null)]
    [InlineData("abcdefghij abcdefghij abcdefghij abcdefg", "abcdefghij abcdefghij abcdefghij abcdefg")]
    public void FoldForReport_KeepsOnlyReportableNames(string input, string? expected)
    {
        Assert.Equal(expected, RosterNameFold.FoldForReport(input));
    }

    [Fact]
    public void NullText_FoldsToEmpty()
    {
        Assert.Equal(string.Empty, RosterNameFold.Fold(null));
    }
}
