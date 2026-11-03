namespace DbdPresence.LocalCi;

/// <summary>
/// Lists the places on this PC the dedicated account may write to (local CI round 3, red-team H1; round 4, F1). A job that gets root inside its Linux machine and then reaches Windows does so as this account,
/// so what the account may write is what such a job could change: the roots of the drives and their top-level folders, the Public folder, the folders in Program Files and in ProgramData, the Windows temp
/// folder and the folders of the system PATH are looked at (one level down, links are not followed). It only reads permissions (through <see cref="IAuditSecurityProbe"/>, over
/// <see cref="AccessRules.AccountMayWrite"/>) and changes nothing. The most serious places come first: a folder in Program Files, in ProgramData or on the PATH that the account may write to lets it place a
/// file that other programs of this PC use.
/// </summary>
/// <remarks>
/// A place whose permissions cannot be read is never "a place the account may not write to": every such place is counted, named with the reason and returned as a gap of the audit, one place at a time, so one
/// refusal can neither end the audit nor pass as an empty answer (round 4, F1). When Windows refuses to show the permissions the read is repeated once with the backup right, where the program holds it.
/// </remarks>
public sealed class AccountWritableLocations : IWritableLocations
{
    private const int Shown = 25;

    private readonly ILocationSource _source;
    private readonly IAuditSecurityProbe _probe;
    private readonly IWslAccount _account;
    private readonly ICiLog _log;

    public AccountWritableLocations(ILocationSource source, IAuditSecurityProbe probe, IWslAccount account, ICiLog log)
    {
        _source = source;
        _probe = probe;
        _account = account;
        _log = log;
    }

    /// <summary>The identities an interactive logon of the account carries, as far as permissions are concerned.</summary>
    public static IReadOnlySet<string> IdentitiesOf(string? accountSid) => new HashSet<string>(StringComparer.Ordinal)
    {
        WellKnownSids.Everyone,
        WellKnownSids.Users,
        WellKnownSids.AuthenticatedUsers,
        "S-1-5-4",
        "S-1-2-0",
        accountSid ?? string.Empty,
    };

    public Task<WritableAudit> FindAsync(CancellationToken cancellationToken) => Task.Run(() => Find(cancellationToken), cancellationToken);

    private WritableAudit Find(CancellationToken cancellationToken)
    {
        var identities = IdentitiesOf(_account.AccountSid());
        var list = _source.List();
        var found = new List<(int Rank, string Text)>();
        var gaps = new List<string>(list.NotListed);
        var looked = 0;
        foreach (var candidate in list.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.NoPermissionsOn is { } fileSystem)
            {
                // FAT, exFAT and the like have no permissions at all: any program may write wherever the drive allows.
                looked++;
                found.Add((1, $"{candidate.Path} ({candidate.Kind}): the file system ({fileSystem}) has no permissions, so the account may write there as any program may"));
                continue;
            }

            var node = Read(candidate, gaps);
            if (node is null)
            {
                continue;
            }

            looked++;
            if (!node.Exists || !node.IsDirectory || node.IsReparsePoint)
            {
                continue;
            }

            var access = AccessRules.AccountMayWrite(node, identities);
            if (access == WriteAccess.None)
            {
                continue;
            }

            var text = $"{candidate.Path} ({candidate.Kind}): the account may {Say(access)}";
            var plantable = (access & (WriteAccess.CreateFiles | WriteAccess.ChangeEntries)) != 0;
            if (plantable && candidate.Kind is LocationKinds.ProgramFiles or LocationKinds.SystemPath)
            {
                text += ". A program placed here can be run by other programs of this PC";
            }
            else if (plantable && candidate.Kind is LocationKinds.ProgramData)
            {
                text += ". A file placed here can be used by the programs of this PC that keep their files here";
            }

            found.Add((Rank(candidate.Kind), text));
        }

        var ordered = found.OrderBy(f => f.Rank).ThenBy(f => f.Text, StringComparer.OrdinalIgnoreCase).Select(f => f.Text).ToList();
        if (ordered.Count > Shown)
        {
            var more = ordered.Count - Shown;
            ordered = [.. ordered.Take(Shown), $"and {more} more places"];
        }

        var named = gaps.Count > WritableAudit.MostGapsNamed
            ? [.. gaps.Take(WritableAudit.MostGapsNamed), $"and {gaps.Count - WritableAudit.MostGapsNamed} more places could not be checked"]
            : gaps;
        if (gaps.Count > 0)
        {
            _log.Warn($"The audit of the places the account may write to could not check {gaps.Count} place(s): {string.Join("; ", gaps.Take(10))}");
        }

        return new WritableAudit(ordered, looked, gaps.Count, named);
    }

    /// <summary>The permissions of one place, or null (and a named gap) when they cannot be read. Whatever Windows says about one place is that place's problem and never ends the audit.</summary>
    private NodeSecurity? Read(LocationCandidate candidate, List<string> gaps)
    {
        try
        {
            return _probe.Describe(candidate.Path);
        }
        catch (UnauthorizedAccessException refused)
        {
            try
            {
                return _probe.DescribeWithBackupRights(candidate.Path);
            }
#pragma warning disable CA1031 // The second read may fail in any way (no backup right, still refused, a damaged answer); the place is named as a gap with both reasons and the audit goes on.
            catch (Exception second) when (second is not OperationCanceledException)
#pragma warning restore CA1031
            {
                gaps.Add($"{candidate.Path} ({candidate.Kind}): {Reason(refused)}; with the backup right: {Reason(second)}");
                return null;
            }
        }
#pragma warning disable CA1031 // See above: one place that cannot be read is a named gap, not the end of the audit.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            gaps.Add($"{candidate.Path} ({candidate.Kind}): {Reason(ex)}");
            return null;
        }
    }

    private static string Reason(Exception ex)
    {
        var first = ex.Message.Split('\n', 2)[0].Trim();
        return first.Length == 0 ? ex.GetType().Name : first.Length > 120 ? first[..120] + "..." : first;
    }

    private static int Rank(string kind) => kind switch
    {
        LocationKinds.ProgramFiles or LocationKinds.SystemPath or LocationKinds.ProgramData => 0,
        LocationKinds.PublicFolder or LocationKinds.WindowsTemp => 1,
        _ => 2,
    };

    private static string Say(WriteAccess access)
    {
        var parts = new List<string>();
        if ((access & WriteAccess.CreateFiles) != 0)
        {
            parts.Add("make files");
        }

        if ((access & WriteAccess.CreateFolders) != 0)
        {
            parts.Add("make folders");
        }

        if ((access & WriteAccess.ChangeEntries) != 0)
        {
            parts.Add("change or delete what is in it");
        }

        if ((access & WriteAccess.ChangePermissions) != 0)
        {
            parts.Add("change who may use it");
        }

        return string.Join(", ", parts);
    }
}
