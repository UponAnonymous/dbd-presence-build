using System.Text.RegularExpressions;

namespace DbdPresence.LocalCi;

/// <summary>Fixed names and limits of the program (docs/owner/free-ci.md).</summary>
public static partial class LocalCiDefaults
{
    public const string RepositoryOwner = "UponAnonymous";
    public const string RepositoryName = "dbd-presence";
    public const string TaskName = "DBD Local CI";

    /// <summary>The label of the one kind of runner this program serves (decision D41: only Linux jobs run on the PC).</summary>
    public const string LinuxLabel = "dbd-linux";
    public const string BaseDistribution = "dbd-ci-base";

    /// <summary>The name of the throwaway Linux machine the isolation probe runs in (round 3, H1). One at a time; it is removed when the probe ends and swept if a crash left it.</summary>
    public const string ProbeDistribution = "dbd-ci-probe";
    public const string JobDistributionPrefix = "dbd-ci-job-";
    public const string LinuxJobUser = "runner";

    /// <summary>The workflow the test check starts by hand (local CI round 2, M4): tiny, on the runner variable, and it says where it ran.</summary>
    public const string ProbeWorkflow = "local-ci-probe.yml";

    /// <summary>The workflow the test check waits for when the probe is not on the repository's default branch yet (GitHub starts a workflow by hand only from there).</summary>
    public const string PassiveWorkflow = "ci-linux.yml";

    public const string RunnerNamePrefix = "dbd-linux-";
    public const int RunnerGroupId = 1;

    /// <summary>The Windows account that owns the WSL machine, its distributions and its settings file (red-team F2): a standard user in no group but Users.</summary>
    public const string WslAccountName = "dbd-ci-wsl";

    /// <summary>
    /// The repository Actions variable that moves Linux jobs to this PC (set by setup, deleted by uninstall). Nothing else is ever set (decision D41): Windows jobs, the release build, deploys and
    /// backups stay on GitHub's machines.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LINUX_RUNNER"] = LinuxLabel,
    };

    /// <summary>
    /// Variables this program never sets (decision D41). If one of them is set, the jobs that read it would wait for a runner this PC does not serve, so setup and status say so and setup offers to
    /// delete it. Uninstall leaves them alone: this program did not make them.
    /// </summary>
    public static IReadOnlyList<string> NotServedVariables { get; } = ["WINDOWS_RUNNER", "SECRETS_LINUX_RUNNER", "SECRETS_WINDOWS_RUNNER"];

    /// <summary>Game programs: while one runs, no new runner starts.</summary>
    public static IReadOnlyList<string> GameProcessNames { get; } =
        ["DeadByDaylight-Win64-Shipping", "DeadByDaylight-EGS-Shipping", "DeadByDaylight"];

    public static TimeSpan JobTimeout { get; } = TimeSpan.FromMinutes(120);

    /// <summary>A runner that waited this long without a job is replaced (fresh image, fresh runner program).</summary>
    public static TimeSpan MaximumWait { get; } = TimeSpan.FromHours(12);

    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(20);

    public static TimeSpan RunnerCheckInterval { get; } = TimeSpan.FromDays(1);

    public static TimeSpan BaseImageMaximumAge { get; } = TimeSpan.FromDays(7);

    public static TimeSpan TokenWarning { get; } = TimeSpan.FromDays(7);

    public static TimeSpan SelfTestTimeout { get; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Until the test workflow is on the default branch the test check judges the next pushed run of CI (Linux) instead. Its jobs go through the one runner one after the other, and each may take up to
    /// the job's own limit (20 minutes) plus the time to start its Linux machine, so the run gets this base plus <see cref="PassiveRunPerJob"/> for every job it has (local CI round 4, QA M-1; the real
    /// times are PENDING-ENV).
    /// </summary>
    public static TimeSpan PassiveRunBase { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The time each job of a pushed run is given (see <see cref="PassiveRunBase"/>).</summary>
    public static TimeSpan PassiveRunPerJob { get; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a pushed run of CI (Linux) with <paramref name="jobCount"/> jobs (at least one) is given to end before the test check gives up waiting for it.</summary>
    public static TimeSpan PassiveRunLimit(int jobCount) => PassiveRunBase + (PassiveRunPerJob * Math.Max(1, jobCount));

    /// <summary>A Linux kernel built longer ago than this is called old by the status (WSL's kernel is updated through <c>wsl --update</c>).</summary>
    public static TimeSpan KernelOldAfter { get; } = TimeSpan.FromDays(180);

    public static int LogRetentionDays => 14;

    public static int RunnerVersionsKept => 2;

    public static int MinimumWindowsBuild => 19041;

    public static long MinimumFreeDiskBytes => 25L * 1024 * 1024 * 1024;

    /// <summary>The largest runner package the program downloads (the real ones are about 100 MB).</summary>
    public static long MaximumRunnerPackageBytes => 600L * 1024 * 1024;

    /// <summary>The most log lines kept of one runner's output.</summary>
    public static int RunnerOutputLinesKept => 5000;

    public static string RunnerName(DateTimeOffset now) => $"{RunnerNamePrefix}{now.UtcDateTime:yyyyMMddHHmmss}";

    /// <summary>True for exactly the name this program gives a runner: <c>dbd-linux-</c> and 14 digits. Nothing else counts as one of its runners (an owner's own runner called <c>dbd-something</c> does not).</summary>
    public static bool IsRunnerName(string? name) => !string.IsNullOrEmpty(name) && CurrentRunnerName().IsMatch(name);

    /// <summary>The name of the throwaway Linux machine of one job for a runner name: <c>dbd-ci-job-</c> and the runner's 14-digit time stamp.</summary>
    public static string JobDistributionFor(string runnerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(runnerName);
        return JobDistributionPrefix + runnerName[(runnerName.LastIndexOf('-') + 1)..];
    }

    /// <summary>
    /// True for exactly the name of a job's Linux machine: <c>dbd-ci-job-</c> and 14 digits. The sweeps of leftovers use this and nothing wider, so they can never touch anything else,
    /// and in particular never the dedicated account <see cref="WslAccountName"/> or a distribution a person made.
    /// </summary>
    public static bool IsJobDistributionName(string? name) => !string.IsNullOrEmpty(name) && JobDistributionName().IsMatch(name);

    /// <summary>The folder name of the Linux machine one base image build uses: <c>dbd-ci-base-</c> and the build's 14-digit time stamp. Every build has its own, so an earlier build's leftovers are never reused.</summary>
    public static string BuildFolderFor(DateTimeOffset now) => $"{BaseDistribution}-{now.UtcDateTime:yyyyMMddHHmmss}";

    /// <summary>True for exactly the folder name of a base image build (see <see cref="BuildFolderFor"/>).</summary>
    public static bool IsBuildFolderName(string? name) => !string.IsNullOrEmpty(name) && BuildFolderName().IsMatch(name);

    /// <summary>The folder name of the Linux machine one isolation probe uses: <c>dbd-ci-probe-</c> and the probe's 14-digit time stamp.</summary>
    public static string ProbeFolderFor(DateTimeOffset now) => $"{ProbeDistribution}-{now.UtcDateTime:yyyyMMddHHmmss}";

    /// <summary>True for exactly the folder name of an isolation probe (see <see cref="ProbeFolderFor"/>).</summary>
    public static bool IsProbeFolderName(string? name) => !string.IsNullOrEmpty(name) && ProbeFolderName().IsMatch(name);

    /// <summary>True for exactly the name of the Linux machine the isolation probe runs in.</summary>
    public static bool IsProbeDistributionName(string? name) => string.Equals(name, ProbeDistribution, StringComparison.Ordinal);

    /// <summary>True for exactly the name of the Linux machine the base image is built in.</summary>
    public static bool IsBaseDistributionName(string? name) => string.Equals(name, BaseDistribution, StringComparison.Ordinal);

    /// <summary>True only for the dedicated account itself (exact name; Windows compares user names without regard to case).</summary>
    public static bool IsWslAccountName(string? name) => string.Equals(name, WslAccountName, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^dbd-linux-[0-9]{14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentRunnerName();

    [GeneratedRegex(@"^dbd-ci-job-[0-9]{14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobDistributionName();

    [GeneratedRegex(@"^dbd-ci-base-[0-9]{14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BuildFolderName();

    [GeneratedRegex(@"^dbd-ci-probe-[0-9]{14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProbeFolderName();
}
