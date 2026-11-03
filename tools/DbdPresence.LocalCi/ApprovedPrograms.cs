namespace DbdPresence.LocalCi;

/// <summary>
/// The list of programs this program may start (security review 2026-10-30, M3): each by its full path, nothing by a bare name. The Windows programs are built from the Windows system folder;
/// a program found elsewhere (this program's installed copy) is added by its full path after it was checked. Anything not on the list is refused.
/// </summary>
public sealed class ApprovedPrograms
{
    private readonly object _gate = new();
    private readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);

    public ApprovedPrograms(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
        Wsl = Path.Combine(systemDirectory, "wsl.exe");
        Schtasks = Path.Combine(systemDirectory, "schtasks.exe");
        Taskkill = Path.Combine(systemDirectory, "taskkill.exe");
        foreach (var program in new[] { Wsl, Schtasks, Taskkill })
        {
            _approved.Add(Path.GetFullPath(program));
        }
    }

    public string Wsl { get; }

    public string Schtasks { get; }

    /// <summary>taskkill.exe, started only by uninstall, with a filter that names the dedicated account (<see cref="TaskkillArguments"/>) and no other.</summary>
    public string Taskkill { get; }

    /// <summary>Adds a program whose full path was found and checked elsewhere.</summary>
    public void Approve(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !Path.IsPathFullyQualified(fullPath))
        {
            throw new InvalidOperationException($"{fullPath} is not a full path, so it cannot be approved to start.");
        }

        lock (_gate)
        {
            _approved.Add(Path.GetFullPath(fullPath));
        }
    }

    /// <summary>Returns the path when it is a full path of an approved program; throws otherwise.</summary>
    public string Require(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !Path.IsPathFullyQualified(fileName))
        {
            throw new InvalidOperationException($"Refusing to start {fileName}: programs are only started by their full path.");
        }

        lock (_gate)
        {
            if (!_approved.Contains(Path.GetFullPath(fileName)))
            {
                throw new InvalidOperationException($"Refusing to start {fileName}: it is not on the list of programs this one may start.");
            }
        }

        return fileName;
    }
}
