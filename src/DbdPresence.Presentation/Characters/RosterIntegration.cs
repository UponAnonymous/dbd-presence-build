using DbdPresence.Configuration;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Packs.Roster;
using DbdPresence.Presentation.Presence;
using DbdPresence.Presentation.ViewModels;
using DbdPresence.Vision.Text;

namespace DbdPresence.Presentation.Characters;

/// <summary>Builds the detection vocabulary from the embedded one and the signed roster (FR-ROS-04, FR-DET-18).</summary>
public static class RosterVocabulary
{
    /// <summary>
    /// The embedded vocabulary plus the roster's new characters. The result changes no detector state: the live loop keeps the vocabulary it was built
    /// with, and the merged one is used the next time the app starts.
    /// </summary>
    /// <param name="embedded">The embedded vocabulary.</param>
    /// <param name="roster">The verified roster.</param>
    /// <exception cref="FormatException">A new character clashes with a known name, code or id; nothing is changed.</exception>
    public static Vocabulary Merge(Vocabulary embedded, RosterDocument roster)
    {
        ArgumentNullException.ThrowIfNull(embedded);
        ArgumentNullException.ThrowIfNull(roster);
        var known = roster.NewCharacters(VocabularyRoster.From(embedded));
        return embedded.WithCharacters(
            known.Where(c => c.Role == DbdPresence.Core.Game.Role.Killer).Select(Entry),
            known.Where(c => c.Role == DbdPresence.Core.Game.Role.Survivor).Select(Entry));
    }

    private static VocabularyEntry Entry(RosterEntry c)
    {
        var aliases = new List<VocabularyAlias>();
        if (!string.Equals(c.ScreenName, c.DisplayName, StringComparison.Ordinal))
        {
            aliases.Add(new VocabularyAlias(c.ScreenName, "signed roster"));
        }

        aliases.AddRange(c.Aliases.Select(a => new VocabularyAlias(a.Name, a.SeenOnScreen)));
        return new VocabularyEntry(c.Id, c.DisplayName, null, aliases, "signed roster", c.Code);
    }
}

/// <summary>
/// The composition root's side of the roster merge (FR-ROS-04): a roster that merges changes the Characters page (new tiles with the New badge) and is
/// checked against the detection vocabulary; one that does not merge throws and changes nothing, so the sync keeps the last good roster.
/// </summary>
public sealed class RosterConsumer : IRosterConsumer
{
    private readonly Vocabulary _embeddedVocabulary;
    private readonly CharacterRoster _embeddedRoster;
    private readonly IUiDispatcher _dispatcher;
    private readonly CharactersViewModel _page;
    private HashSet<string>? _applied;

    /// <summary>Initializes a new instance of the <see cref="RosterConsumer"/> class.</summary>
    /// <param name="embeddedVocabulary">The embedded vocabulary.</param>
    /// <param name="page">The Characters page.</param>
    /// <param name="dispatcher">Runs the page update on the UI thread.</param>
    public RosterConsumer(Vocabulary embeddedVocabulary, CharactersViewModel page, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(embeddedVocabulary);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _embeddedVocabulary = embeddedVocabulary;
        _embeddedRoster = VocabularyRoster.From(embeddedVocabulary);
        _page = page;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public void Apply(RosterDocument roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var merged = roster.MergeInto(_embeddedRoster);
        _ = RosterVocabulary.Merge(_embeddedVocabulary, roster);
        var codes = roster.NewCharacters(_embeddedRoster).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

        // The first roster of a run is the one the app already had (no badge); later ones badge the codes the page had not seen.
        IReadOnlySet<string>? fresh = _applied is null ? null : codes.Where(c => !_applied.Contains(c)).ToHashSet(StringComparer.Ordinal);
        _applied = codes;
        _dispatcher.Post(() => _page.SetRoster(merged, fresh));
    }
}

/// <summary>Moves the character pictures of imported packs into the picture model once (FR-PACK-16, PLAN_V2_S5B §2.6).</summary>
public static class ImportedPackMigrationRunner
{
    /// <summary>
    /// Runs <see cref="ImportedPackMigration"/> over the imported packs. The pictures and the one-time notice go into the settings; the packs are removed
    /// only when every picture was copied, and the pack choices that named a removed pack are cleared. Running it again with no imported packs does nothing.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="library">The pack library.</param>
    /// <param name="store">Copies the files into the pictures folder.</param>
    /// <returns>The result, or null when there was nothing to move.</returns>
    public static ImportedPackMigrationResult? Run(ISettingsService settings, PackLibrary library, PictureStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        var packs = library.Imported;
        if (packs.Count == 0)
        {
            return null;
        }

        var current = settings.Current;
        var result = ImportedPackMigration.Migrate(packs, current.Pictures, current.Packs.CharactersPack, store);
        var notice = result.GenericKeys.Count > 0 || result.OverLimit > 0
            ? new PictureMigrationNotice { GenericOverrides = result.GenericKeys, OverLimit = result.OverLimit }
            : current.PictureNotice;
        var issues = settings.Update(s => s with { Pictures = result.Pictures, PictureNotice = notice });

        // The source packs go only when the settings really hold what was copied: no issue was raised (a dropped picture or a failed save would be one),
        // and every migrated character key and picture is in the saved settings (round-2 QA review F2). Otherwise they stay and the next start tries again.
        var saved = settings.Current.Pictures;
        var held = issues.Count == 0
            && result.Pictures.All(kv => saved.TryGetValue(kv.Key, out var pictures) && pictures.Items.Count >= kv.Value.Items.Count);
        if (!result.AllCopied || !held)
        {
            return result;
        }

        var removed = packs.Select(p => p.Info.Id).ToHashSet(StringComparer.Ordinal);
        _ = settings.Update(s => s with
        {
            Packs = s.Packs with
            {
                KillerPack = removed.Contains(s.Packs.KillerPack) ? string.Empty : s.Packs.KillerPack,
                SurvivorPack = removed.Contains(s.Packs.SurvivorPack) ? string.Empty : s.Packs.SurvivorPack,
                CharactersPack = removed.Contains(s.Packs.CharactersPack) ? string.Empty : s.Packs.CharactersPack,
                MapsPack = removed.Contains(s.Packs.MapsPack) ? string.Empty : s.Packs.MapsPack,
            },
        });
        foreach (var id in removed)
        {
            library.RemoveImported(id);
        }

        return result;
    }
}
