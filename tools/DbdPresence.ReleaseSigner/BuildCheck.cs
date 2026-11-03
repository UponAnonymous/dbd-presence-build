using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DbdPresence.ReleaseSigner;

/// <summary>Which service a packaged app build talks to: the one compiled into its <c>DbdPresence.Online.dll</c> (ADR-0016).</summary>
internal enum PackagedService
{
    /// <summary>A build made without <c>-p:OnlineEnvironment=Production</c>: it talks to the staging host.</summary>
    Staging,

    /// <summary>A build made with <c>-p:OnlineEnvironment=Production</c>.</summary>
    Production,
}

/// <summary>
/// Reads which service a full update package was built for (threat-model review 2026-10-25, TM-02), so a staging build is never
/// signed into a production feed. It opens the package, finds <c>DbdPresence.Online.dll</c> and reads the string literals its code
/// uses (the metadata's user-string heap). Only code literals count: a staging build loads the staging host as a literal, while a
/// production build keeps the staging host only as an unused constant, which lives in another table. The file is parsed, never loaded
/// or run.
/// </summary>
internal static class BuildCheck
{
    /// <summary>The <c>set</c> names of TrustedServiceKeys.json.</summary>
    public const string ProductionSet = "production";

    /// <summary>The <c>set</c> name of the staging keys.</summary>
    public const string StagingSet = "staging";

    private const string OnlineAssembly = "DbdPresence.Online.dll";
    private const long MaxAssemblyBytes = 32L * 1024 * 1024;

    // The first label of each staging host (src/DbdPresence.Online/ServiceEnvironment.cs). Labels only: this tool holds no host name.
    private static readonly string[] s_stagingMarkers = ["api-staging", "cdn-staging"];

    // The production API host without its top-level domain, for the same reason.
    private const string ProductionMarker = "api.arcavale";

    /// <summary>Throws unless <paramref name="set"/> is <c>production</c> or <c>staging</c>.</summary>
    /// <param name="set">The key set the feed is signed for.</param>
    /// <returns>The set.</returns>
    public static string RequireSet(string set) =>
        set is ProductionSet or StagingSet ? set : throw new SignerException($"--set is production or staging, not '{set}'.");

    /// <summary>Reads which service the package's app build talks to.</summary>
    /// <param name="packagePath">The <c>.nupkg</c>.</param>
    /// <returns>The service.</returns>
    /// <exception cref="InvalidDataException">The package has no single readable <c>DbdPresence.Online.dll</c>, or it names neither service.</exception>
    public static PackagedService Read(string packagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        var name = Path.GetFileName(packagePath);
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{name}' is not a readable package.", ex);
        }

        byte[] bytes;
        using (zip)
        {
            var entries = zip.Entries.Where(e => string.Equals(e.Name, OnlineAssembly, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count != 1)
            {
                throw new InvalidDataException($"'{name}' has {entries.Count} copies of {OnlineAssembly}, not one, so it cannot be checked. Use the package the Release workflow made.");
            }

            if (entries[0].Length is < 1 or > MaxAssemblyBytes)
            {
                throw new InvalidDataException($"{OnlineAssembly} in '{name}' has an unexpected size ({entries[0].Length} bytes).");
            }

            using var stream = entries[0].Open();
            using var copy = new MemoryStream((int)entries[0].Length);
            stream.CopyTo(copy);
            bytes = copy.ToArray();
        }

        return FromAssembly(bytes, name);
    }

    /// <summary>Reads the service of one <c>DbdPresence.Online.dll</c> image.</summary>
    /// <param name="assembly">The assembly's bytes.</param>
    /// <param name="packageName">The package it came from, for messages.</param>
    /// <returns>The service.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a .NET assembly, or it names neither service.</exception>
    internal static PackagedService FromAssembly(byte[] assembly, string packageName)
    {
        var staging = false;
        var production = false;
        try
        {
            using var pe = new PEReader(new MemoryStream(assembly, writable: false));
            if (!pe.HasMetadata)
            {
                throw new InvalidDataException($"{OnlineAssembly} in '{packageName}' is not a .NET assembly.");
            }

            var reader = pe.GetMetadataReader();
            for (var handle = reader.GetNextHandle(default(UserStringHandle)); !handle.IsNil; handle = reader.GetNextHandle(handle))
            {
                var literal = reader.GetUserString(handle);
                staging |= s_stagingMarkers.Any(m => literal.Contains(m, StringComparison.OrdinalIgnoreCase));
                production |= literal.Contains(ProductionMarker, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"{OnlineAssembly} in '{packageName}' is not a readable .NET assembly.", ex);
        }

        if (staging)
        {
            return PackagedService.Staging;
        }

        return production
            ? PackagedService.Production
            : throw new InvalidDataException($"{OnlineAssembly} in '{packageName}' names neither the staging nor the production service, so it cannot be checked.");
    }

    /// <summary>Throws unless the package was built for <paramref name="set"/>.</summary>
    /// <param name="packagePath">The <c>.nupkg</c>.</param>
    /// <param name="set"><c>production</c> or <c>staging</c>.</param>
    /// <exception cref="SignerException">The package was built for the other service, or cannot be checked.</exception>
    public static void Require(string packagePath, string set)
    {
        RequireSet(set);
        PackagedService found;
        try
        {
            found = Read(packagePath);
        }
        catch (InvalidDataException ex)
        {
            throw new SignerException(ex.Message, ex);
        }

        var wanted = set == ProductionSet ? PackagedService.Production : PackagedService.Staging;
        if (found != wanted)
        {
            throw new SignerException(
                $"{Path.GetFileName(packagePath)} is a {Describe(found)} build, but you are signing for {set}. Nothing was signed. " +
                (wanted == PackagedService.Production
                    ? "Take the package from the Release workflow's artifact (docs/owner/release-keys.md step 6), not from CI (Windows)."
                    : "Sign a staging feed only with packages from CI (Windows)."));
        }
    }

    private static string Describe(PackagedService service) => service == PackagedService.Production ? "production" : "staging";
}
