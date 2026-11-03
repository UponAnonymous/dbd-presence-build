namespace DbdPresence.Packs.Import;

/// <summary>Image formats the import accepts, identified by magic bytes only (FR-PACK-07).</summary>
public enum ImportImageFormat
{
    /// <summary>PNG.</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>WebP.</summary>
    WebP,
}

/// <summary>What a file name suggests an image is for.</summary>
public enum ProposalKind
{
    /// <summary>A role tile such as killer or survivor.</summary>
    Role,

    /// <summary>A character portrait.</summary>
    Character,

    /// <summary>A map image.</summary>
    Map,
}

/// <summary>What in a file name gave a proposal (PLAN_V2_S5B §2.7).</summary>
public enum ProposalBasis
{
    /// <summary>The name, a slug or an alias in the file name.</summary>
    Name,

    /// <summary>The game's character code at the start of the file name (<c>K27_TheOnryo_Portrait.png</c>).</summary>
    Code,
}

/// <summary>
/// A suggested mapping of an imported file. It is only a suggestion: nothing is mapped until the user confirms it
/// (FR-PACK-07), so <see cref="NeedsUserConfirmation"/> is always true.
/// </summary>
/// <param name="Kind">Role, character or map.</param>
/// <param name="Slug">The known slug the file name matched.</param>
/// <param name="Basis">Whether the file name's character code or its name gave the match.</param>
public sealed record MappingProposal(ProposalKind Kind, string Slug, ProposalBasis Basis = ProposalBasis.Name)
{
    /// <summary>The pack mapping key such as <c>character:the-trapper</c>.</summary>
    public string Key => $"{Kind.ToString().ToLowerInvariant()}:{Slug}";

    /// <summary>Always true: a proposal never maps anything by itself.</summary>
    public bool NeedsUserConfirmation { get; } = true;
}

/// <summary>An image that passed every check. The bytes are still untrusted until the image helper decodes them.</summary>
/// <param name="DisplayName">A sanitised file name for display; never used as a path.</param>
/// <param name="Bytes">The raw image bytes, read in memory.</param>
/// <param name="Format">The format detected from magic bytes.</param>
/// <param name="Proposal">A suggested mapping, or null when the name matched nothing known.</param>
public sealed record ImportCandidate(string DisplayName, byte[] Bytes, ImportImageFormat Format, MappingProposal? Proposal);

/// <summary>An archive entry that was not imported.</summary>
/// <param name="Name">The sanitised entry name for display.</param>
/// <param name="Reason">Why it was skipped, in words a user can read.</param>
public sealed record SkippedEntry(string Name, string Reason);

/// <summary>The outcome of reading an import archive.</summary>
/// <param name="Candidates">Accepted images.</param>
/// <param name="Skipped">Entries that were left out, with reasons.</param>
/// <param name="RejectedReason">Set when the whole archive was refused; then there are no candidates.</param>
public sealed record ImportResult(IReadOnlyList<ImportCandidate> Candidates, IReadOnlyList<SkippedEntry> Skipped, string? RejectedReason)
{
    /// <summary>True when the whole archive was refused.</summary>
    public bool IsRejected => RejectedReason is not null;

    /// <summary>A refusal of the whole archive.</summary>
    public static ImportResult Reject(string reason, IReadOnlyList<SkippedEntry>? skipped = null) => new([], skipped ?? [], reason);
}

/// <summary>The hardening limits for reading an archive (FR-PACK-07).</summary>
public sealed record ImportLimits
{
    /// <summary>The shipped limits.</summary>
    public static ImportLimits Default { get; } = new();

    /// <summary>Largest archive in bytes.</summary>
    public long MaxArchiveBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Most entries in an archive.</summary>
    public int MaxEntries { get; init; } = 500;

    /// <summary>Most bytes read from one entry.</summary>
    public long MaxEntryBytes { get; init; } = 2L * 1024 * 1024;

    /// <summary>Most bytes read from all entries together.</summary>
    public long MaxTotalBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Most bytes of clean PNGs kept in memory from one import (the images the user can still choose from); further images are listed as skipped.</summary>
    public long MaxResultBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Largest inflated-to-compressed ratio of one entry.</summary>
    public long MaxRatio { get; init; } = 100;
}
