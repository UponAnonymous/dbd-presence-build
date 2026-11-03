using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>What one attempt to get out of the Linux machine came to.</summary>
public enum ProbeVerdict
{
    /// <summary>The attempt was stopped: the way out is closed.</summary>
    Blocked,

    /// <summary>The attempt worked: the way out is open.</summary>
    Possible,

    /// <summary>The attempt could not be made (a tool was missing, the script stopped early). Counts as not proven closed.</summary>
    NotTried,
}

/// <summary>One attempt and what it showed.</summary>
/// <param name="Id">The attempt's id, one of <see cref="IsolationProbe.AttemptIds"/>.</param>
/// <param name="Verdict">What came of it.</param>
/// <param name="Evidence">What was seen, in a few plain words (never a secret, never a name of a person's file or account).</param>
public sealed record ProbeAttempt(string Id, ProbeVerdict Verdict, string Evidence);

/// <summary>Everything the isolation probe found out.</summary>
/// <param name="Attempts">The attempts in the order of <see cref="IsolationProbe.AttemptIds"/>; an attempt that did not report is <see cref="ProbeVerdict.NotTried"/>.</param>
/// <param name="Audit">The places on this PC the dedicated account may write to (what a job that gets out of its Linux machine could change), and the places the audit could not look at.</param>
/// <param name="Failure">Why the probe could not run at all, or null when it ran.</param>
public sealed record ProbeReport(IReadOnlyList<ProbeAttempt> Attempts, WritableAudit Audit, string? Failure)
{
    /// <summary>The places the account may write to, in plain words.</summary>
    public IReadOnlyList<string> WritableLocations => Audit.Places;

    /// <summary>True when the probe ran and every attempt was stopped. The audit of writable places is not part of this; see <see cref="AllBlocked"/>.</summary>
    public bool AttemptsAllBlocked => Failure is null && Attempts.Count > 0 && Attempts.All(a => a.Verdict == ProbeVerdict.Blocked);

    /// <summary>True when the probe ran, every attempt was stopped and the audit of writable places looked at every place it meant to (round 4, F1: an incomplete audit is not a clean one).</summary>
    public bool AllBlocked => AttemptsAllBlocked && Audit.Complete;

    /// <summary>True when at least one attempt worked.</summary>
    public bool AnythingPossible => Attempts.Any(a => a.Verdict == ProbeVerdict.Possible);

    public ProbeAttempt? Attempt(string id) => Attempts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>The word kept in the state file: AllBlocked, AuditIncomplete (every attempt stopped but some places could not be checked), SomePossible or Incomplete.</summary>
    public string Outcome => Failure is not null ? "Incomplete" : AnythingPossible ? "SomePossible" : AllBlocked ? "AllBlocked" : AttemptsAllBlocked ? "AuditIncomplete" : "Incomplete";
}

/// <summary>
/// The isolation probe (local CI round 3, red-team H1): the program tries, on purpose and with the owner watching, what a job would do if it gained root inside its Linux machine. It imports the base image
/// as the dedicated account into a throwaway machine, and as root there tries to mount a Windows drive and list <c>C:\Users</c>, to write a marker file into <c>C:\Users\Public</c> (which the elevated
/// program then looks for on the Windows side, and removes: that exact path and a plain file only), to bring the Windows program interface back and start <c>cmd.exe /c whoami</c>, and it looks for the GPU and
/// virtualization devices, for shared (9p, virtiofs) mounts and for the network of the host: the PC's address answering on the common ports. Each attempt is BLOCKED or POSSIBLE, with the evidence. The
/// result is kept in the state file and shown by the status; set-up turns the Linux checks on only after the owner has typed "yes" to it.
/// </summary>
/// <remarks>
/// The probe is evidence about the installed WSL and its settings, not a proof: PENDING-ENV, no probe has run on a Windows PC yet. It does not change the PC; the machine it uses is removed afterwards; the
/// only thing it can leave on Windows is its marker file, and that is looked for and removed by name.
/// </remarks>
public sealed partial class IsolationProbe
{
    /// <summary>The attempts, in the order the script makes them and the report shows them.</summary>
    public static IReadOnlyList<string> AttemptIds { get; } =
    [
        "mount-windows-drive", "write-windows-public", "windows-programs", "gpu-device", "virtualization-device", "shared-mounts", "host-network",
    ];

    /// <summary>
    /// Run as root in the probe machine, after <see cref="ScriptFor"/> has put the marker file's name in front. Prints <c>ATTEMPT id BLOCKED|POSSIBLE|NOTTRIED evidence</c>. The line <c>R=""</c> is the root
    /// folder prefix tests replace (see <see cref="WslPreflight.Script"/>). Pure bash and the tools every image has; the host's port test uses bash's own <c>/dev/tcp</c> under <c>timeout</c>.
    /// </summary>
    public const string Script = """
        set -u
        R=""
        say() { printf 'ATTEMPT %s %s %s\n' "$1" "$2" "${3:0:180}"; }
        first_line() { printf '%s' "$1" | tr -d '\r' | head -n 1; }
        mp="$R/mnt/dbd-probe"
        mkdir -p "$mp" 2>/dev/null
        mounted=0
        if out="$(mount -t drvfs 'C:' "$mp" 2>&1)"; then
          mounted=1
          n="$(ls -A "$mp/Users" 2>/dev/null | wc -l)"
          if [ "$n" -gt 0 ]; then
            say mount-windows-drive POSSIBLE "root mounted C: and listed $n entries in C:\\Users"
          else
            say mount-windows-drive POSSIBLE "root mounted C: (C:\\Users could not be listed)"
          fi
        else
          say mount-windows-drive BLOCKED "mount -t drvfs failed: $(first_line "$out")"
        fi
        if [ "$mounted" = "1" ]; then
          if printf 'dbd-ci isolation probe\n' > "$mp/Users/Public/$MARKER" 2>/dev/null; then
            say write-windows-public POSSIBLE "wrote $MARKER into C:\\Users\\Public"
          else
            say write-windows-public BLOCKED "C: was mounted but C:\\Users\\Public could not be written"
          fi
        else
          say write-windows-public BLOCKED "no Windows drive could be mounted, so there was nothing to write into"
        fi
        registered=0
        if [ ! -e "$R/proc/sys/fs/binfmt_misc/register" ]; then mount -t binfmt_misc binfmt_misc "$R/proc/sys/fs/binfmt_misc" >/dev/null 2>&1; fi
        printf ':WSLInterop:M::MZ::/init:PF\n' > "$R/proc/sys/fs/binfmt_misc/register" 2>/dev/null
        if [ -e "$R/proc/sys/fs/binfmt_misc/WSLInterop" ]; then registered=1; fi
        socket=0
        for s in "$R"/run/WSL/*; do
          case "$s" in
            *_interop) if [ -e "$s" ]; then socket=1; fi ;;
            *) if [ -S "$s" ]; then socket=1; fi ;;
          esac
        done
        if [ -n "${WSL_INTEROP:-}" ] && [ -e "$R$WSL_INTEROP" ]; then socket=1; fi
        cmd=""
        if command -v cmd.exe >/dev/null 2>&1; then cmd=cmd.exe; elif [ "$mounted" = "1" ] && [ -x "$mp/Windows/System32/cmd.exe" ]; then cmd="$mp/Windows/System32/cmd.exe"; fi
        if [ -n "$cmd" ]; then
          answer="$(timeout 10 "$cmd" /c whoami 2>&1)"
          rc=$?
          who="$(first_line "$answer")"
          case "$who" in
            *\\*) say windows-programs POSSIBLE "started a Windows program; whoami said: $who" ;;
            *)
              if [ "$rc" = "126" ]; then
                say windows-programs BLOCKED "a Windows program was found but the system refused to run it (exit code 126: $who)"
              else
                say windows-programs NOTTRIED "a Windows program was found but gave no answer an account could be read from, which does not show that it cannot run (exit code $rc: $who)"
              fi ;;
          esac
        elif [ "$registered" = "1" ] && [ "$socket" = "1" ]; then
          say windows-programs POSSIBLE "the Windows program interface is registered and its server answers on /run/WSL, so a program placed in the Linux files could probably be started"
        else
          say windows-programs BLOCKED "no Windows program could be started (interface registered: $registered, interop server socket: $socket)"
        fi
        if [ "$mounted" = "1" ]; then umount "$mp" >/dev/null 2>&1; fi
        rmdir "$mp" 2>/dev/null
        if [ -e "$R/dev/dxg" ]; then say gpu-device POSSIBLE "the GPU device /dev/dxg exists"; else say gpu-device BLOCKED "there is no /dev/dxg"; fi
        if [ -e "$R/dev/kvm" ]; then say virtualization-device POSSIBLE "the virtualization device /dev/kvm exists"; else say virtualization-device BLOCKED "there is no /dev/kvm"; fi
        shares=""
        while read -r dev mnt type rest; do
          case "$type" in
            9p|virtiofs|drvfs|cifs|smb3|nfs|nfs4|vboxsf|vmhgfs) shares="$shares $mnt($type)" ;;
          esac
        done 2>/dev/null < "$R/proc/mounts"
        if [ -n "$shares" ]; then say shared-mounts POSSIBLE "mounted from outside the machine:$shares"; else say shared-mounts BLOCKED "no 9p, virtiofs, drvfs or network file system is mounted"; fi
        gateway=""
        while read -r iface dest gate rest; do
          if [ "$dest" = "00000000" ] && [ "$iface" != "Iface" ]; then gateway="$gate"; break; fi
        done 2>/dev/null < "$R/proc/net/route"
        neighbours="$(grep -c '^[0-9]' "$R/proc/net/arp" 2>/dev/null)"
        if [ "${#gateway}" -eq 8 ]; then
          address="$((16#${gateway:6:2})).$((16#${gateway:4:2})).$((16#${gateway:2:2})).$((16#${gateway:0:2}))"
          open=""
          for port in 135 139 445 3389 5985 5986 80 443 8080; do
            if timeout 1 bash -c "exec 3<>/dev/tcp/$address/$port" >/dev/null 2>&1; then open="$open $port"; fi
          done
          if [ -n "$open" ]; then
            say host-network POSSIBLE "the PC's address $address answered on port(s):$open (neighbours in the table: ${neighbours:-0})"
          else
            say host-network BLOCKED "the PC's address $address answered on none of the common ports (neighbours in the table: ${neighbours:-0})"
          fi
        else
          say host-network NOTTRIED "the machine has no default route, so there was no network to try"
        fi
        """;

    private readonly IWsl _wsl;
    private readonly WslSandbox _sandbox;
    private readonly LocalCiPaths _paths;
    private readonly IntegrityStore _integrity;
    private readonly IProbeMarker _marker;
    private readonly IWritableLocations _locations;
    private readonly ResidueRecorder _residue;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;
    private readonly TimeProvider _time;

    public IsolationProbe(IWsl wsl, WslSandbox sandbox, LocalCiPaths paths, IntegrityStore integrity, IProbeMarker marker, IWritableLocations locations, ResidueRecorder residue, ICiLog log, Redactor redactor, TimeProvider time)
    {
        _wsl = wsl;
        _sandbox = sandbox;
        _paths = paths;
        _integrity = integrity;
        _marker = marker;
        _locations = locations;
        _residue = residue;
        _log = log;
        _redactor = redactor;
        _time = time;
    }

    /// <summary>The text that runs in the probe machine: the marker's name in front of <see cref="Script"/>. The name is checked to have the exact shape of a marker before it goes into the text.</summary>
    public static string ScriptFor(string markerName)
    {
        if (!ProbeMarkerNames.IsMarker(markerName))
        {
            throw new ArgumentException("The marker file name is not one this program makes.", nameof(markerName));
        }

        return $"MARKER='{markerName}'\n{Script}";
    }

    /// <summary>
    /// Names the setup a probe result is about: the Linux machine settings, the base image recipe and the probe's own text. A result for another setup says nothing about this one, so the status asks for a
    /// new probe when the name differs.
    /// </summary>
    public static string RecipeIdFor(WslHostFacts facts, string baseImageRecipe)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrEmpty(baseImageRecipe);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WslSettingsPolicy.ExpectedText(facts) + "\n" + baseImageRecipe + "\n" + Script)))[..16];
    }

    /// <summary>The attempts out of what the script printed. An attempt that did not report, or reported twice differently, is read against the Linux machine (not proven closed).</summary>
    public static IReadOnlyList<ProbeAttempt> Parse(IEnumerable<string> lines, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var seen = new Dictionary<string, ProbeAttempt>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var match = AttemptLine().Match(raw.Replace("\0", string.Empty, StringComparison.Ordinal).Trim());
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            var verdict = match.Groups["verdict"].Value switch
            {
                "BLOCKED" => ProbeVerdict.Blocked,
                "POSSIBLE" => ProbeVerdict.Possible,
                _ => ProbeVerdict.NotTried,
            };
            var attempt = new ProbeAttempt(id, verdict, match.Groups["evidence"].Value);
            if (seen.TryGetValue(id, out var earlier) && Weight(earlier.Verdict) >= Weight(attempt.Verdict))
            {
                continue;
            }

            seen[id] = attempt;
        }

        var result = new List<ProbeAttempt>();
        foreach (var id in AttemptIds)
        {
            result.Add(seen.TryGetValue(id, out var attempt) ? attempt : new ProbeAttempt(id, ProbeVerdict.NotTried, exitCode != 0 ? $"the probe script stopped (exit code {exitCode}) before this attempt" : "the probe did not report this attempt"));
        }

        return result;
    }

    /// <summary>
    /// Runs the probe: the Windows-side audit of what the account may write, then the attempts in a throwaway machine as the dedicated account. Never throws for a failure of the probe itself (it comes back as
    /// <see cref="ProbeReport.Failure"/> and counts as not proven); only a cancel passes through. The machine and the marker file are removed in every case.
    /// </summary>
    public Task<ProbeReport> RunAsync(CancellationToken cancellationToken) => RunCoreAsync(withAudit: true, cancellationToken);

    /// <summary>
    /// The attempts only, without the audit of what the account may write (local CI round 4, red-team F5): what the loop repeats by itself after the program's own WSL update. The audit reads Windows permissions
    /// and may switch the backup right on; that stays with the commands the owner starts and watches. The report's audit is <see cref="WritableAudit.NotMade"/>, so it is never taken for a clean one.
    /// </summary>
    public Task<ProbeReport> RunAttemptsOnlyAsync(CancellationToken cancellationToken) => RunCoreAsync(withAudit: false, cancellationToken);

    private async Task<ProbeReport> RunCoreAsync(bool withAudit, CancellationToken cancellationToken)
    {
        var audit = withAudit ? await AuditAsync(cancellationToken).ConfigureAwait(false) : WritableAudit.NotMade;
        var markerName = _marker.NewName();
        var markerFinding = ProbeMarkerFinding.NotThere;
        IReadOnlyList<ProbeAttempt> attempts = [];
        string? failure = null;
        try
        {
            var run = await RunInMachineAsync(markerName, cancellationToken).ConfigureAwait(false);
            attempts = run.Attempts;
            if (run.Exit != 0)
            {
                // The script ends with exit code 0 when it ran to its last line. Anything else (killed, out of memory, a shell error) means what it printed may be all there is of a script that did not do what it is
                // written to do: the lines are kept to be read, but the result is not a complete one and can neither be accepted nor renewed (round 4, F6).
                failure = $"The isolation probe script ended with exit code {run.Exit} instead of 0, so its result cannot be taken as complete: nothing is shown to be closed.";
                _log.Failure(failure);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A probe that cannot run is a result ("not proven"), told in words; it must never stop set-up with a stack trace.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = $"The isolation probe could not run: {_redactor.Describe(ex)}";
            _log.Failure(failure);
        }
        finally
        {
            markerFinding = LookForTheMarker(markerName);
        }

        if (attempts.Count == 0)
        {
            var reason = failure ?? "the probe did not run";
            if (markerFinding == ProbeMarkerFinding.NotThere)
            {
                return new ProbeReport(IncompleteAttempts(reason), audit, failure ?? reason);
            }

            attempts = IncompleteAttempts(reason);
        }

        return new ProbeReport(SeenFromWindows(attempts, markerName, markerFinding), audit, failure);
    }

    private static int Weight(ProbeVerdict verdict) => verdict switch
    {
        ProbeVerdict.Possible => 2,
        ProbeVerdict.NotTried => 1,
        _ => 0,
    };

    private static List<ProbeAttempt> IncompleteAttempts(string reason) => [.. AttemptIds.Select(id => new ProbeAttempt(id, ProbeVerdict.NotTried, reason))];

    /// <summary>The marker file is looked for on the Windows side, whatever the Linux side said: a file that is there proves the write, a file that is not there does not disprove it.</summary>
    private static IReadOnlyList<ProbeAttempt> SeenFromWindows(IReadOnlyList<ProbeAttempt> attempts, string markerName, ProbeMarkerFinding finding)
    {
        if (finding == ProbeMarkerFinding.NotThere)
        {
            return attempts;
        }

        var seen = finding == ProbeMarkerFinding.FoundAndRemoved
            ? $"the marker file {markerName} appeared in C:\\Users\\Public on Windows (seen from Windows, and removed)"
            : $"the marker file {markerName} appeared in C:\\Users\\Public on Windows and could not be removed (delete it by hand)";
        return [.. attempts.Select(a => a.Id == "write-windows-public" ? new ProbeAttempt(a.Id, ProbeVerdict.Possible, seen) : a)];
    }

    private ProbeMarkerFinding LookForTheMarker(string markerName)
    {
        try
        {
            var finding = _marker.FindAndRemove(markerName);
            if (finding == ProbeMarkerFinding.FoundButNotRemoved)
            {
                _residue.Add(ResidueKinds.MarkerFile, markerName, $"the marker file {markerName} of an isolation probe is in C:\\Users\\Public (delete it by hand)");
            }

            return finding;
        }
        catch (IOException ex)
        {
            _log.Failure($"Could not look for the probe's marker file on Windows: {_redactor.Describe(ex)}");
            _residue.Add(ResidueKinds.MarkerFile, markerName, $"the marker file {markerName} of an isolation probe may be in C:\\Users\\Public (look for it and delete it by hand)");
            return ProbeMarkerFinding.NotThere;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Failure($"Could not look for the probe's marker file on Windows: {_redactor.Describe(ex)}");
            _residue.Add(ResidueKinds.MarkerFile, markerName, $"the marker file {markerName} of an isolation probe may be in C:\\Users\\Public (look for it and delete it by hand)");
            return ProbeMarkerFinding.NotThere;
        }
    }

    private async Task<WritableAudit> AuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _locations.FindAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The audit is information for the owner; a failure of it never stops the probe, but it is not an empty answer either: it comes back as an audit that could not be made (round 4, F1).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var reason = _redactor.Describe(ex);
            _log.Failure($"Could not list the places the account may write to: {reason}");
            return WritableAudit.Failed(reason);
        }
    }

    /// <summary>What the probe script printed, read as attempts, and the exit code it ended with.</summary>
    private sealed record MachineRun(IReadOnlyList<ProbeAttempt> Attempts, int Exit);

    private async Task<MachineRun> RunInMachineAsync(string markerName, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.BaseArchive) || _integrity.Recorded(_paths.BaseArchive) is null)
        {
            throw new JobEnvironmentException("the Linux base image has not been built yet, so there is no machine to try anything in");
        }

        var distribution = LocalCiDefaults.ProbeDistribution;
        var directory = _paths.WslInstallDirectory(LocalCiDefaults.ProbeFolderFor(_time.GetUtcNow()));
        var session = await _sandbox.BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                var present = await _wsl.ListDistributionsAsync(cancellationToken).ConfigureAwait(false);
                if (present.Contains(distribution, StringComparer.OrdinalIgnoreCase))
                {
                    _log.Warn($"The Linux machine {distribution} of an earlier probe is still there; removing it.");
                    if (!await _sandbox.RemoveDistributionAsync(distribution).ConfigureAwait(false))
                    {
                        throw new JobEnvironmentException($"The Linux machine {distribution} of an earlier probe could not be removed, so no new probe was started.");
                    }

                    _residue.Resolve(ResidueKinds.LinuxMachine, distribution);
                }

                var baseImage = await _integrity.OpenVerifiedAsync(_paths.BaseArchive, cancellationToken).ConfigureAwait(false);
                await using (baseImage.ConfigureAwait(false))
                {
                    await _wsl.ImportAsync(distribution, directory, baseImage, cancellationToken).ConfigureAwait(false);
                }

                var lines = new ConcurrentQueue<string>();
                var exit = await _wsl.ExecuteAsync(distribution, "root", ScriptFor(markerName), null, lines.Enqueue, WslLimits.Probe, cancellationToken).ConfigureAwait(false);
                return new MachineRun(Parse(lines, exit), exit);
            }
            finally
            {
                if (await _sandbox.RemoveDistributionAsync(distribution).ConfigureAwait(false))
                {
                    _residue.Resolve(ResidueKinds.LinuxMachine, distribution);
                    if (_sandbox.RemoveFolderIfEmpty(directory))
                    {
                        _residue.Resolve(ResidueKinds.Folder, directory);
                    }
                    else
                    {
                        _residue.Add(ResidueKinds.Folder, directory, $"the folder {directory} of an isolation probe is not empty (delete it by hand when no Linux machine uses it)");
                    }
                }
                else
                {
                    _residue.Add(ResidueKinds.LinuxMachine, distribution, $"the Linux machine {distribution} of an isolation probe could not be removed");
                }
            }
        }
    }

    [GeneratedRegex(@"^ATTEMPT (?<id>[a-z][a-z-]{0,40}) (?<verdict>BLOCKED|POSSIBLE|NOTTRIED)(?: (?<evidence>.{0,200}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptLine();
}
