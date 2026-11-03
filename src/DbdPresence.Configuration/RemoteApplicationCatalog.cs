using DbdPresence.Core.Online;

namespace DbdPresence.Configuration;

/// <summary>
/// The application catalog with the signed remote list merged in (FR-ONL-08, ADR-0012, D8). The remote list replaces the bundled one
/// only when it is non-empty; the bundled list stays the fallback. The remote default (if any) is listed first. Everything else in the
/// remote configuration is ignored here: this class can only change <i>which</i> official application ids are offered.
/// </summary>
public sealed class RemoteApplicationCatalog : IApplicationCatalog
{
    private readonly IApplicationCatalog _fallback;
    private readonly IRemoteConfigSource _remote;

    /// <summary>Initializes a new instance of the <see cref="RemoteApplicationCatalog"/> class.</summary>
    /// <param name="fallback">The bundled catalog.</param>
    /// <param name="remote">The merged remote configuration.</param>
    public RemoteApplicationCatalog(IApplicationCatalog fallback, IRemoteConfigSource remote)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(remote);
        _fallback = fallback;
        _remote = remote;
    }

    /// <inheritdoc />
    public IReadOnlyList<OfficialApplication> Applications
    {
        get
        {
            var config = _remote.Current;
            if (config.Applications.Count == 0)
            {
                return _fallback.Applications;
            }

            var all = config.Applications.Select(a => new OfficialApplication(a.Key, a.ApplicationId, a.Name, a.Description)).ToList();
            var defaultApp = all.FirstOrDefault(a => a.Key == config.DefaultApplicationKey);
            if (defaultApp is not null)
            {
                all.Remove(defaultApp);
                all.Insert(0, defaultApp);
            }

            return all;
        }
    }

    /// <inheritdoc />
    public OfficialApplication Resolve(string? key)
    {
        var applications = Applications;
        return applications.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal)) ?? applications[0];
    }
}
