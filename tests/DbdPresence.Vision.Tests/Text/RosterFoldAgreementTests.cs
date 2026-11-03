// Covers: PLAN_V2_S5B §2.2 check 4 (the service's port of the OCR fold agrees with the app's own normaliser), D35 (a learned name that the service
// checked never collides inside the vocabulary). The fixture tests/fixtures/roster/fold-cases.json is also checked by the service and Core suites.
using System.Text.Json;
using DbdPresence.Vision.Text;

namespace DbdPresence.Vision.Tests.Text;

public sealed class RosterFoldAgreementTests
{
    public static TheoryData<string, string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "roster", "fold-cases.json")));
            foreach (var c in json.RootElement.GetProperty("cases").EnumerateArray())
            {
                // Names without a fold are refused by the service and the reporter; the normaliser keeps such letters, so they are not compared.
                if (c.GetProperty("fold").ValueKind == JsonValueKind.String)
                {
                    data.Add(c.GetProperty("input").GetString()!, c.GetProperty("fold").GetString()!, c.GetProperty("skeleton").GetString()!);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheServiceFoldAndSkeleton_AreTheNormalisers(string input, string fold, string skeleton)
    {
        Assert.Equal(fold, TextNormalizer.Normalize(input));
        Assert.Equal(skeleton, TextNormalizer.Skeleton(input));
    }
}
