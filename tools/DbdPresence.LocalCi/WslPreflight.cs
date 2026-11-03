using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>The outcome of the isolation check inside a job's Linux environment.</summary>
/// <param name="Passed">True only when every required check reported PASS and the script ended cleanly.</param>
/// <param name="Failures">Plain-words reasons, empty when passed.</param>
public sealed record PreflightResult(bool Passed, IReadOnlyList<string> Failures)
{
    /// <summary>The kernel boot id the machine reported (null when it did not report a well-formed one).</summary>
    public string? BootId { get; init; }

    /// <summary>The kernel release the machine reported (<c>uname -r</c>), or null.</summary>
    public string? KernelRelease { get; init; }

    /// <summary>When the kernel says it was built (the date in <c>uname -v</c>), or null when there is none or its zone is not UTC.</summary>
    public DateTimeOffset? KernelBuilt { get; init; }
}

/// <summary>
/// The check every Linux job environment must pass before its runner starts (security review 2026-10-30, M4; round 3, M3). It is fail-closed: a check that is missing from the output,
/// a check that says FAIL and a script that ends with an error all stop the job, and the environment is removed. Since round 3 it proves the effect of the settings, not only their text: the
/// <c>.wslconfig</c> file is a request, and a WSL that does not know a key ignores it without a message, so the checks look at the machine itself (its memory, processors, swap, devices and mounts, its
/// age and boot id, whether a program can gain privileges, whether a user namespace can be made).
/// </summary>
/// <remarks>
/// Which mounts a WSL 2 machine always has (from the open WSL source and Microsoft's documentation, read 2026-10-30; whether this holds for the installed WSL is PENDING-ENV, shown by the isolation probe
/// and the first-run table): the root file system (ext4 on the virtual disk), <c>/dev</c>, <c>/proc</c>, <c>/sys</c>, <c>/run</c>, <c>/dev/pts</c>, <c>/dev/shm</c>, the cgroups and <c>/mnt/wsl</c> (memory
/// file systems the kernel and WSL's init make: acceptable, they hold nothing of Windows). The mounts that carry Windows into the machine are only made when a feature is on: <c>/mnt/c</c> and the other
/// drives (9p or virtiofs, <c>hostFileSystemAccess</c>), <c>/usr/lib/wsl/drivers</c> and <c>/usr/lib/wsl/lib</c> (<c>gpuSupport</c>), <c>/mnt/wslg</c> and the Linux window system's shares
/// (<c>guiApplications</c>). All three are switched off in the account's settings, so a machine that has any 9p, virtiofs, drvfs or network file system mounted is not as it was asked to be: it fails
/// (check <c>no-host-shares</c>), and nothing is allowed by name. If a real WSL turns out to always carry one more mount of that kind, the rule is to be narrowed on that evidence, not guessed here.
/// </remarks>
public static partial class WslPreflight
{
    /// <summary>The longest a new machine may have been running when the check looks at it: it was started by this job (the import takes minutes), not by an earlier one.</summary>
    public const int MaximumUptimeSeconds = 1800;

    /// <summary>The check ids every run must report, in the order the script prints them.</summary>
    public static IReadOnlyList<string> RequiredChecks { get; } =
    [
        "not-root", "runner-user", "no-windows-drives", "no-drvfs-mount", "no-windows-programs", "no-gui", "no-sudo",
        "no-host-shares", "no-gpu-device", "no-kvm", "memory-cap", "cpu-cap", "no-swap", "fresh-boot", "recent-start", "no-new-privs", "no-user-namespaces", "kernel-info",
    ];

    /// <summary>
    /// The script text for one job: the limits of this PC's Linux machine and the boot id of the last job's machine go in as four assignments in front of <see cref="Script"/>. The boot id is checked
    /// to be a well-formed identifier before it is put into the text; anything else becomes "none".
    /// </summary>
    /// <param name="facts">The limits the account's settings gave the machine.</param>
    /// <param name="lastBootId">The boot id the last job's machine reported, or null.</param>
    public static string ScriptFor(WslHostFacts facts, string? lastBootId)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var last = lastBootId is not null && BootIdShape().IsMatch(lastBootId) ? lastBootId : "none";
        var memoryKilobytes = (long)facts.MemoryGigabytes * 1024 * 1024;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"CAP_MEM_KB={memoryKilobytes}\nCAP_CPUS={facts.Processors}\nLAST_BOOT_ID='{last}'\nMAX_UPTIME={MaximumUptimeSeconds}\n{Script}");
    }

    /// <summary>
    /// Run as the <c>runner</c> user inside the job's distribution (after <see cref="ScriptFor"/> has put its four parameters in front; run on its own it stops at the first one it needs, so every check
    /// after it is missing and the job is refused). Prints <c>CHECK id PASS</c> or <c>CHECK id FAIL reason</c> lines; a PASS line may carry a fact (<c>fresh-boot</c> the boot id, <c>kernel-info</c> the
    /// kernel). A Windows drive (or the folder of the Linux window system) counts only when its folder is a mount point or has something in it: an empty folder named <c>/mnt/c</c> that the image
    /// carries is only a name (red-team round 2, QA M12). The line <c>R=""</c> is the root folder prefix of every path; the tests of this script replace that one line by a fake root and run the rest
    /// unchanged. No environment variable can change it (red-team round 3, M3: the old <c>DBD_TEST_ROOT</c> hook was removed from the shipped text).
    /// </summary>
    public const string Script = """
        set -u
        R=""
        pass() { printf 'CHECK %s PASS\n' "$1"; }
        passinfo() { printf 'CHECK %s PASS %s\n' "$1" "$2"; }
        fail() { printf 'CHECK %s FAIL %s\n' "$1" "${2:0:180}"; }
        holds() {
          [ -d "$1" ] || return 1
          if grep -Fq " ${1#"$R"} " "$R/proc/mounts" 2>/dev/null; then return 0; fi
          [ -n "$(ls -A "$1" 2>/dev/null)" ]
        }
        meminfo() {
          local key="$1" k v rest
          while read -r k v rest; do
            if [ "$k" = "$key:" ]; then printf '%s' "$v"; return 0; fi
          done 2>/dev/null < "$R/proc/meminfo"
          return 1
        }
        if [ "$(id -u)" = "0" ]; then fail not-root "the job would run as root"; else pass not-root; fi
        if [ "$(id -un)" = "runner" ]; then pass runner-user; else fail runner-user "the user is $(id -un), not runner"; fi
        drives=""
        for d in "$R"/mnt/[A-Za-z]; do
          if holds "$d"; then drives="$drives ${d#"$R"}"; fi
        done
        if [ -n "$drives" ]; then fail no-windows-drives "a Windows drive folder is mounted or not empty:$drives"; else pass no-windows-drives; fi
        if grep -Eq 'drvfs|path=[A-Za-z]:' "$R/proc/mounts" 2>/dev/null; then fail no-drvfs-mount "a Windows drive is mounted"; else pass no-drvfs-mount; fi
        if [ -e "$R/proc/sys/fs/binfmt_misc/WSLInterop" ] || [ -e "$R/proc/sys/fs/binfmt_misc/WSLInterop-late" ]; then
          fail no-windows-programs "the Windows program interface is switched on"
        elif command -v cmd.exe >/dev/null 2>&1 || command -v powershell.exe >/dev/null 2>&1; then
          fail no-windows-programs "cmd.exe or powershell.exe can be found"
        else
          pass no-windows-programs
        fi
        if holds "$R/mnt/wslg"; then fail no-gui "the folder of the Linux window system, /mnt/wslg, is mounted or not empty"; else pass no-gui; fi
        if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then fail no-sudo "sudo works without a password"; else pass no-sudo; fi
        shares=""
        while read -r dev mnt type rest; do
          case "$type" in
            9p|virtiofs|drvfs|cifs|smb3|nfs|nfs4|vboxsf|vmhgfs) shares="$shares $mnt($type)" ;;
          esac
        done 2>/dev/null < "$R/proc/mounts"
        if [ -n "$shares" ]; then fail no-host-shares "a file system shared from outside the machine is mounted:$shares"; else pass no-host-shares; fi
        if [ -e "$R/dev/dxg" ]; then fail no-gpu-device "the GPU device /dev/dxg exists"; else pass no-gpu-device; fi
        if [ -e "$R/dev/kvm" ]; then fail no-kvm "the virtualization device /dev/kvm exists"; else pass no-kvm; fi
        mem="$(meminfo MemTotal)" || mem=""
        if [ -n "$mem" ] && [ "$mem" -le "$CAP_MEM_KB" ] 2>/dev/null; then pass memory-cap; else fail memory-cap "the machine has ${mem:-an unknown amount of} kB of memory; the limit is $CAP_MEM_KB kB"; fi
        cpus="$(nproc --all 2>/dev/null)" || cpus=""
        if [ -n "$cpus" ] && [ "$cpus" -ge 1 ] 2>/dev/null && [ "$cpus" -le "$CAP_CPUS" ] 2>/dev/null; then pass cpu-cap; else fail cpu-cap "the machine has ${cpus:-an unknown number of} processors; the limit is $CAP_CPUS"; fi
        swap="$(meminfo SwapTotal)" || swap=""
        if [ "$swap" = "0" ]; then pass no-swap; else fail no-swap "the machine has swap (${swap:-unknown} kB)"; fi
        boot=""
        { read -r boot < "$R/proc/sys/kernel/random/boot_id"; } 2>/dev/null
        if [[ "$boot" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]; then
          if [ "$boot" = "$LAST_BOOT_ID" ]; then fail fresh-boot "the machine has the boot id of the last job's machine, so it was not started fresh"; else passinfo fresh-boot "$boot"; fi
        else
          fail fresh-boot "the boot id of the machine could not be read"
        fi
        up=""
        { read -r up _ < "$R/proc/uptime"; } 2>/dev/null
        secs="${up%%.*}"
        if [ -n "$secs" ] && [ "$secs" -le "$MAX_UPTIME" ] 2>/dev/null; then pass recent-start; else fail recent-start "the machine has been running for ${secs:-an unknown time} seconds; a new one has not run for more than $MAX_UPTIME"; fi
        flag=""
        if command -v setpriv >/dev/null 2>&1; then
          flag="$(setpriv --no-new-privs sh -c 'while read -r k v; do if [ "$k" = "NoNewPrivs:" ]; then echo "$v"; fi; done 2>/dev/null < "$1"' sh "$R/proc/self/status" 2>/dev/null)"
        fi
        if [ "$flag" = "1" ]; then pass no-new-privs; else fail no-new-privs "setpriv --no-new-privs is not available or does not set the flag"; fi
        namespaces=""
        { read -r namespaces < "$R/proc/sys/user/max_user_namespaces"; } 2>/dev/null
        if [ "$namespaces" != "0" ]; then
          fail no-user-namespaces "user namespaces are allowed (limit ${namespaces:-unknown})"
        elif command -v unshare >/dev/null 2>&1 && unshare -U true >/dev/null 2>&1; then
          fail no-user-namespaces "a user namespace can be made although the limit is 0"
        else
          pass no-user-namespaces
        fi
        release="$(uname -r 2>/dev/null)" || release=""
        version="$(uname -v 2>/dev/null)" || version=""
        if [ -n "$release" ] && [ -n "$version" ]; then passinfo kernel-info "${release:0:60}|${version:0:100}"; else fail kernel-info "the kernel version could not be read"; fi
        """;

    public static PreflightResult Parse(IEnumerable<string> lines, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var reported = new Dictionary<string, (bool Pass, string Detail)>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var raw in lines)
        {
            var match = CheckLine().Match(raw.Replace("\0", string.Empty, StringComparison.Ordinal).Trim());
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            var pass = match.Groups["verdict"].Value == "PASS";
            var detail = match.Groups["detail"].Value;
            if (reported.TryGetValue(id, out var earlier) && earlier.Pass != pass)
            {
                failures.Add($"{id}: reported both PASS and FAIL");
            }

            reported[id] = reported.TryGetValue(id, out var previous) && !previous.Pass ? previous : (pass, detail);
        }

        foreach (var required in RequiredChecks)
        {
            if (!reported.TryGetValue(required, out var result))
            {
                failures.Add($"{required}: the check did not report");
            }
            else if (!result.Pass)
            {
                failures.Add($"{required}: {(result.Detail.Length > 0 ? result.Detail : "failed")}");
            }
        }

        // Two checks pass with a fact: a pass that does not carry it is not the script's own output (the boot id is what proves the machine is not the last job's).
        if (reported.TryGetValue("fresh-boot", out var freshBoot) && freshBoot.Pass && !BootIdShape().IsMatch(freshBoot.Detail))
        {
            failures.Add("fresh-boot: the check passed without reporting the boot id of the machine");
        }

        if (reported.TryGetValue("kernel-info", out var kernelInfo) && kernelInfo.Pass && kernelInfo.Detail.Trim().Length == 0)
        {
            failures.Add("kernel-info: the check passed without reporting the kernel");
        }

        foreach (var (id, result) in reported.Where(r => !r.Value.Pass && !RequiredChecks.Contains(r.Key)))
        {
            failures.Add($"{id}: {(result.Detail.Length > 0 ? result.Detail : "failed")}");
        }

        if (exitCode != 0)
        {
            failures.Add($"the check script ended with exit code {exitCode}");
        }

        string? bootId = null;
        if (reported.TryGetValue("fresh-boot", out var boot) && boot.Pass && BootIdShape().IsMatch(boot.Detail))
        {
            bootId = boot.Detail;
        }

        string? release = null;
        DateTimeOffset? built = null;
        if (reported.TryGetValue("kernel-info", out var kernel) && kernel.Pass)
        {
            var bar = kernel.Detail.IndexOf('|', StringComparison.Ordinal);
            release = (bar < 0 ? kernel.Detail : kernel.Detail[..bar]).Trim();
            built = bar < 0 ? null : ParseKernelBuilt(kernel.Detail[(bar + 1)..]);
        }

        return new PreflightResult(failures.Count == 0, failures) { BootId = bootId, KernelRelease = release is { Length: > 0 } ? release : null, KernelBuilt = built };
    }

    /// <summary>
    /// The build date in the text <c>uname -v</c> prints (<c>#1 SMP PREEMPT_DYNAMIC Thu Jun  5 18:30:46 UTC 2025</c>): read when the zone is UTC or GMT, otherwise null (the age is then shown as not
    /// known, never guessed).
    /// </summary>
    public static DateTimeOffset? ParseKernelBuilt(string? version)
    {
        if (version is null || KernelBuiltDate().Match(version) is not { Success: true } match)
        {
            return null;
        }

        if (match.Groups["zone"].Value is not ("UTC" or "GMT"))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            $"{match.Groups["day"].Value} {match.Groups["month"].Value} {match.Groups["year"].Value} {match.Groups["time"].Value}",
            "d MMM yyyy HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : null;
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BootIdShape();

    [GeneratedRegex(@"\b(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+(?<day>\d{1,2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<zone>[A-Z]{2,5})\s+(?<year>\d{4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex KernelBuiltDate();

    [GeneratedRegex(@"^CHECK (?<id>[a-z][a-z-]{0,40}) (?<verdict>PASS|FAIL)(?: (?<detail>.{0,200}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckLine();
}
