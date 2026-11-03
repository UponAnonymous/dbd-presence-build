using System.Globalization;

namespace DbdPresence.Vision.Prompts;

/// <summary>The outcome of the agreement rule for one match so far.</summary>
/// <param name="KillerId">The killer the prompts agree on, or null while they do not.</param>
/// <param name="Score">The raw score of the agreement (the weakest contributing prompt), 0 when there is none.</param>
/// <param name="DistinctPrompts">How many distinct mapped prompts of one killer were seen.</param>
/// <param name="Reason">One plain sentence for the evidence string. It never contains screen text.</param>
public sealed record PromptAgreement(string? KillerId, double Score, int DistinctPrompts, string Reason)
{
    /// <summary>Gets a value indicating whether the prompts agree on a killer.</summary>
    public bool Agrees => KillerId is not null;
}

/// <summary>
/// What the match's power prompts have shown so far, as an immutable value (the pipeline holds the current one and hands it to the detector through
/// the detection context, so the detector stays a pure function). One mapped prompt is never enough to name a killer: the rule needs
/// <see cref="RequiredDistinctPrompts"/> distinct mapped prompts of one killer, or one mapped prompt together with another cue that named the same
/// killer in this match (the owner's own killer row on TAB or the scoreboard). Prompts of two killers, or a prompt that contradicts the other cue,
/// give no agreement at all.
/// </summary>
public sealed class PromptEvidence
{
    /// <summary>How many distinct mapped prompts of one killer must be seen when no other cue is present (an initial guess; the owner's rule is "at least two").</summary>
    public const int RequiredDistinctPrompts = 2;

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> _byKiller;

    private PromptEvidence(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> byKiller, string? corroboratedKiller)
    {
        _byKiller = byKiller;
        CorroboratedKiller = corroboratedKiller;
    }

    /// <summary>Gets the evidence of a match in which nothing was seen.</summary>
    public static PromptEvidence Empty { get; } = new(new Dictionary<string, IReadOnlyDictionary<string, double>>(), null);

    /// <summary>Gets the killer another cue named in this match, or null.</summary>
    public string? CorroboratedKiller { get; }

    /// <summary>Gets how many distinct mapped prompts were seen, over all killers.</summary>
    public int DistinctPrompts => _byKiller.Values.Sum(v => v.Count);

    /// <summary>Returns the evidence after one more accepted prompt (a prompt seen before only keeps its best score).</summary>
    /// <param name="match">The accepted match of the current frame; a match that identified nothing changes nothing.</param>
    public PromptEvidence With(PromptMatch match)
    {
        if (!match.Accepted || match.PromptId is null || match.KillerId is null)
        {
            return this;
        }

        var next = _byKiller.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, double>)new Dictionary<string, double>(p.Value), StringComparer.Ordinal);
        var prompts = next.TryGetValue(match.KillerId, out var existing) ? new Dictionary<string, double>(existing, StringComparer.Ordinal) : new Dictionary<string, double>(StringComparer.Ordinal);
        prompts[match.PromptId] = Math.Max(prompts.GetValueOrDefault(match.PromptId), match.Score);
        next[match.KillerId] = prompts;
        return new PromptEvidence(next, CorroboratedKiller);
    }

    /// <summary>Returns the evidence after another cue (the owner's killer row) named a killer in this match.</summary>
    /// <param name="killerId">The killer id.</param>
    public PromptEvidence WithCorroboration(string killerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(killerId);
        return string.Equals(CorroboratedKiller, killerId, StringComparison.Ordinal) ? this : new PromptEvidence(_byKiller, killerId);
    }

    /// <summary>Applies the agreement rule.</summary>
    public PromptAgreement Evaluate()
    {
        if (_byKiller.Count == 0)
        {
            return new PromptAgreement(null, 0, 0, "no mapped power prompt seen");
        }

        if (_byKiller.Count > 1)
        {
            return new PromptAgreement(null, 0, 0, "mapped prompts of more than one killer were seen: no agreement");
        }

        var (killer, prompts) = _byKiller.Single();
        var distinct = prompts.Count;
        if (CorroboratedKiller is not null)
        {
            return string.Equals(CorroboratedKiller, killer, StringComparison.Ordinal)
                ? new PromptAgreement(killer, prompts.Values.Min(), distinct, "a mapped power prompt and the owner's killer row name the same killer")
                : new PromptAgreement(null, 0, distinct, "a mapped power prompt contradicts the owner's killer row: no agreement");
        }

        return distinct >= RequiredDistinctPrompts
            ? new PromptAgreement(killer, prompts.Values.OrderDescending().Take(RequiredDistinctPrompts).Min(), distinct, string.Create(CultureInfo.InvariantCulture, $"{distinct} distinct mapped power prompts name the same killer"))
            : new PromptAgreement(null, 0, distinct, string.Create(CultureInfo.InvariantCulture, $"{distinct} of {RequiredDistinctPrompts} distinct mapped power prompts seen: no agreement yet"));
    }
}
