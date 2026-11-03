using System.Text;
using DbdPresence.Configuration;
using DbdPresence.Core.Pictures;
using DbdPresence.Packs.Local;
using DbdPresence.Packs.Pictures;
using DbdPresence.Packs.Roster;

namespace DbdPresence.Presentation.Characters;

/// <summary>
/// The one place that turns a character, the user's settings and the pack library into the picture the preview shows and the address Discord is given
/// (D29, FR-ART-04, FR-PACK-06). The Characters page and the presence artwork both call it, so the preview shows exactly what Discord gets. It reads
/// files and settings only; it never starts a network call and never saves anything online (FR-PACK-13).
/// </summary>
public sealed class CharacterPictureResolver
{
    private readonly PackLibrary _library;
    private readonly PictureStore _store;
    private readonly IPictureEnvironment _environment;
    private CharacterRoster _roster;

    /// <summary>Initializes a new instance of the <see cref="CharacterPictureResolver"/> class.</summary>
    /// <param name="library">The pack library.</param>
    /// <param name="store">Finds the files of the user's own pictures.</param>
    /// <param name="environment">Account, sync and saving-online facts.</param>
    /// <param name="roster">The characters; a name from a detection is looked up here.</param>
    public CharacterPictureResolver(PackLibrary library, PictureStore store, IPictureEnvironment environment, CharacterRoster roster)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roster);
        _library = library;
        _store = store;
        _environment = environment;
        _roster = roster;
    }

    /// <summary>Replaces the roster after a sync added characters (D27).</summary>
    /// <param name="roster">The merged roster.</param>
    public void SetRoster(CharacterRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        Volatile.Write(ref _roster, roster);
    }

    /// <summary>Builds the resolver's input from the library and the environment as they are now.</summary>
    /// <param name="settings">The settings.</param>
    public PictureContext BuildContext(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var saved = _environment.SavedUrlBySha256;
        return PictureContextBuilder.Build(
            _library,
            _store,
            settings.Packs.CharactersPack,
            _environment.DefaultPackId,
            _environment.SourceShaByEntryId,
            item => OnlineOf(item, saved, settings.PictureRules),
            settings.PictureRules);
    }

    // The account facts first (saved, signed out, rules, blocked); a picture still "saving" then takes the saving-online queue's reason, so the
    // page says "storage full", "saving resumes tomorrow" or "couldn't save online" instead of "saving" (PLAN_V2_S5B §2.6).
    private PictureOnline OnlineOf(PictureItem item, IReadOnlyDictionary<string, string> saved, PictureRules rules)
    {
        var basic = PictureOnline.Basic(item, saved, _environment.SignedIn, rules, _environment.UploadsBlocked);
        return basic.Status == PictureOnlineStatus.Saving && _environment.SavingStatusOf(item.Sha256) is { } waiting ? new PictureOnline(waiting) : basic;
    }

    /// <summary>Resolves a character named by a detection or the user, or returns null when the roster does not know the name.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="characterName">The display name, an alias or the pack slug.</param>
    public PictureResolution? Resolve(AppSettings settings, string? characterName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var character = Find(characterName);
        if (character is null)
        {
            return null;
        }

        var key = PictureKeys.Of(character.Id);
        return PictureResolver.Resolve(key, character.Role, settings.Pictures.GetValueOrDefault(key), BuildContext(settings));
    }

    /// <summary>Folds a name for comparison: accents and case removed, letters and digits only.</summary>
    /// <param name="value">The text.</param>
    internal static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                _ = builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private RosterCharacter? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var folded = Fold(name);
        if (folded.Length == 0)
        {
            return null;
        }

        return Volatile.Read(ref _roster).Characters.FirstOrDefault(c =>
            Fold(c.Name) == folded || Fold(c.Id) == folded || c.OtherNames.Any(o => Fold(o) == folded));
    }
}
