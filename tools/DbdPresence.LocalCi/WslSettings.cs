using System.Text;

namespace DbdPresence.LocalCi;

/// <summary>One setting of a <c>.wslconfig</c> file: the section it is in (empty before the first section), its name and its value.</summary>
public sealed record WslConfigEntry(string Section, string Key, string Value);

/// <summary>
/// A <c>.wslconfig</c> file (WSL's settings for one Windows user: <c>%UserProfile%\.wslconfig</c>), read as entries. Names are compared without regard to case, every occurrence of a
/// key counts, and a line that is neither a section, a setting, a comment nor empty is kept apart (<see cref="UnreadableLines"/>) so that nothing hides in it.
/// </summary>
public sealed class WslConfigFile
{
    private readonly List<WslConfigEntry> _entries = [];
    private readonly List<string> _unreadable = [];

    private WslConfigFile()
    {
    }

    /// <summary>Every setting in file order.</summary>
    public IReadOnlyList<WslConfigEntry> Entries => _entries;

    /// <summary>The lines that are not a section, a setting, a comment or empty.</summary>
    public IReadOnlyList<string> UnreadableLines => _unreadable;

    public static WslConfigFile Parse(string? text)
    {
        var file = new WslConfigFile();
        var section = string.Empty;
        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']' && line.Length > 2)
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                file._unreadable.Add(line.Length > 80 ? line[..80] + "..." : line);
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value[..comment].TrimEnd();
            }

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            file._entries.Add(new WslConfigEntry(section, key, value));
        }

        return file;
    }

    /// <summary>Every value given for <paramref name="key"/> in <paramref name="section"/>, in file order (empty when none).</summary>
    public IReadOnlyList<string> GetAll(string section, string key) =>
        [.. _entries.Where(e => string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value)];
}

/// <summary>What this PC has, for the limits of the Linux machine.</summary>
/// <param name="TotalMemoryBytes">The PC's physical memory.</param>
/// <param name="LogicalProcessors">The PC's logical processors.</param>
public sealed record WslHostFacts(long TotalMemoryBytes, int LogicalProcessors)
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Half of the PC's memory, but at least 2 GB and at most 8 GB.</summary>
    public int MemoryGigabytes => (int)Math.Clamp(TotalMemoryBytes / 2 / Gigabyte, 2, 8);

    /// <summary>Half of the PC's logical processors, but at least 1 and at most 4.</summary>
    public int Processors => Math.Clamp(LogicalProcessors / 2, 1, 4);
}

/// <summary>
/// The WSL settings of the dedicated account (local CI round 2, red-team F2 and F8; round 3, H1, M1, L3): exactly these twelve, nothing else. The program writes the file and reads it back before every job
/// and refuses to start the Linux machine when it differs, byte for byte. Keys and meaning are from Microsoft's page "Advanced settings configuration in WSL" and from the open WSL source
/// (<c>WslCoreConfig.h</c>, read 2026-10-30); a key that a given WSL does not know is skipped by WSL without a message, which is why the checks inside the machine (see <see cref="WslPreflight"/>)
/// prove the effect and do not rest on the file alone:
/// <list type="bullet">
/// <item><c>networkingMode=nat</c>: the default network (the guest cannot take the host's network identity as <c>mirrored</c> would).</item>
/// <item><c>localhostForwarding=false</c>: a port opened in Linux is not reachable from Windows as <c>localhost</c>.</item>
/// <item><c>guiApplications=false</c>: no Linux window system (and with it no system distribution that would mount a Windows share for the window system).</item>
/// <item><c>swap=0</c>: no swap file, so nothing of a job is paged to a file on the PC's disk.</item>
/// <item><c>memory</c> and <c>processors</c>: what the Linux machine may take from the PC.</item>
/// <item><c>nestedVirtualization=false</c>: no <c>/dev/kvm</c> in the guest (Windows 11 only, per the documentation).</item>
/// <item><c>gpuSupport=false</c>: no GPU device (<c>/dev/dxg</c>) and no GPU driver shares in the machine.</item>
/// <item><c>hostFileSystemAccess=false</c>: WSL does not give the machine any share of the Windows file system, so not even root in the guest can mount a Windows drive (<c>mount -t drvfs</c>). In the WSL
/// source, <c>InitializeDrvFs</c> is only started when this is true. The key is not in Microsoft's documentation table (only named in the row for <c>virtiofs</c>); an older WSL ignores it, and the
/// isolation probe shows whether it works.</item>
/// <item><c>defaultVhdSize=40GB</c>: the largest disk a Linux machine made by <c>wsl --import</c> may grow to (the default is 1 TB), so a job cannot fill the PC's drive through its own disk (red-team M1).</item>
/// <item><c>autoProxy=false</c>: the machine does not take over the Windows account's proxy settings (Windows 11 only).</item>
/// <item><c>firewall=true</c>: the Windows firewall and the Hyper-V firewall rules filter the machine's network traffic (the default, written down so a changed default cannot switch it off; Windows 11 22H2 and later).</item>
/// </list>
/// </summary>
public static class WslSettingsPolicy
{
    public const string Section = "wsl2";

    /// <summary>The disk size limit written as <c>defaultVhdSize</c>.</summary>
    public const string DiskLimit = "40GB";

    /// <summary>The settings that are fixed (the memory and processor limits depend on the PC).</summary>
    public static IReadOnlyList<(string Key, string Value)> FixedSettings { get; } =
    [
        ("networkingMode", "nat"),
        ("localhostForwarding", "false"),
        ("guiApplications", "false"),
        ("swap", "0"),
    ];

    /// <summary>The fixed settings that come after the memory and processor limits in the file.</summary>
    public static IReadOnlyList<(string Key, string Value)> HardeningSettings { get; } =
    [
        ("nestedVirtualization", "false"),
        ("gpuSupport", "false"),
        ("hostFileSystemAccess", "false"),
        ("defaultVhdSize", DiskLimit),
        ("autoProxy", "false"),
        ("firewall", "true"),
    ];

    /// <summary>The settings in the order they are written.</summary>
    public static IReadOnlyList<(string Key, string Value)> Expected(WslHostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return
        [
            .. FixedSettings,
            ("memory", facts.MemoryGigabytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "GB"),
            ("processors", facts.Processors.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            .. HardeningSettings,
        ];
    }

    /// <summary>The file text the program writes.</summary>
    public static string ExpectedText(WslHostFacts facts)
    {
        var text = new StringBuilder();
        text.Append('[').Append(Section).Append("]\r\n");
        foreach (var (key, value) in Expected(facts))
        {
            text.Append(key).Append('=').Append(value).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>The file as it is written to disk: UTF-8 without a byte order mark, lines ending in CR LF.</summary>
    public static byte[] ExpectedBytes(WslHostFacts facts) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ExpectedText(facts));

    /// <summary>True when the two texts say exactly the same settings (line endings, spacing and comments do not matter).</summary>
    public static bool SameSettings(string? text, WslHostFacts facts) => Problems(text, facts).Count == 0;

    /// <summary>
    /// What is wrong with the file as it is on disk: the settings (see <see cref="Problems"/>) and then the bytes. The file must be exactly what the program writes (UTF-8 without a byte order mark, CR LF
    /// line ends, this order, no comment): an encoding mark or another line ending that WSL happens to read the same is still refused, so what is checked is what was written (red-team round 3, L3).
    /// A missing file is a problem; so is text that is not valid UTF-8.
    /// </summary>
    public static IReadOnlyList<string> BytesProblems(byte[]? content, WslHostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (content is null)
        {
            return Problems(null, facts);
        }

        // A leading UTF-8 mark is set aside so that the settings themselves can still be judged; the mark is then named as the reason when nothing else is wrong.
        var mark = content.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        var body = mark ? content.AsSpan(Encoding.UTF8.GetPreamble().Length) : content.AsSpan();
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return ["the file is not valid UTF-8 text"];
        }

        var problems = Problems(text, facts).ToList();
        if (problems.Count == 0 && (mark || !body.SequenceEqual(ExpectedBytes(facts))))
        {
            problems.Add("the file says the right settings but is not byte for byte what this program writes (an encoding mark, other line endings, spacing or comments)");
        }

        return problems;
    }

    /// <summary>
    /// What is wrong with the file: anything but exactly the expected settings. A missing file, a missing setting, a changed value, a duplicate, another setting, another section, a setting outside
    /// any section and a line that is not a setting are all problems.
    /// </summary>
    public static IReadOnlyList<string> Problems(string? text, WslHostFacts facts)
    {
        var problems = new List<string>();
        if (text is null)
        {
            problems.Add("the settings file is not there");
            return problems;
        }

        var file = WslConfigFile.Parse(text);
        foreach (var line in file.UnreadableLines)
        {
            problems.Add($"the file has a line that is not a setting: {line}");
        }

        var expected = Expected(facts);
        foreach (var (key, value) in expected)
        {
            var found = file.GetAll(Section, key);
            if (found.Count == 0)
            {
                problems.Add($"{key} is missing (it must be {value})");
            }
            else if (found.Count > 1)
            {
                problems.Add($"{key} is given {found.Count} times");
            }
            else if (!string.Equals(found[0], value, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{key} is {found[0]} (it must be {value})");
            }
        }

        foreach (var entry in file.Entries)
        {
            var known = string.Equals(entry.Section, Section, StringComparison.OrdinalIgnoreCase) && expected.Any(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                problems.Add(entry.Section.Length == 0 ? $"the setting {entry.Key} is outside any section" : $"the setting {entry.Key} in [{entry.Section}] is not one this program writes");
            }
        }

        return problems;
    }
}
