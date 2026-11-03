using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace DbdPresence.LocalCi.Tests;

/// <summary>The maintenance of the loop built from fakes, with every part within reach of a test.</summary>
internal sealed class MaintenanceRig : IDisposable
{
    private readonly TempFolder _folder = new();

    /// <param name="tokenCheckLimit">Passed to the maintenance (its default is 2 minutes).</param>
    /// <param name="runnerDownloadLimit">Passed to the maintenance (its default is 30 minutes).</param>
    /// <param name="imageBuildLimit">Passed to the maintenance (its default is 90 minutes).</param>
    /// <param name="withDownloadableImage">True to make the Ubuntu file downloadable (a small test file), so a base image build gets as far as the Linux machine.</param>
    /// <param name="withWslUpdate">True to give the maintenance the weekly WSL update (with a fake installer, the fake process list and a switch the test can turn).</param>
    /// <param name="withProbeRenewal">True to give the maintenance the isolation probe that is repeated after the program's own WSL update, and the gate over the accepted probe (round 4, F5); the state then holds an accepted probe.</param>
    public MaintenanceRig(TimeSpan? tokenCheckLimit = null, TimeSpan? runnerDownloadLimit = null, TimeSpan? imageBuildLimit = null, bool withDownloadableImage = false, bool withWslUpdate = false, bool withProbeRenewal = false)
    {
        Time = TestTime.New();
        Redactor = new Redactor();
        Log = new MemoryCiLog(Redactor);
        Api = new FakeGitHubApi { Release = new RunnerRelease("2.338.0", string.Empty, new Uri("https://github.com/a")) };
        Downloads = new FakeDownloads();
        Wsl = new FakeWsl();
        Processes = new FakeProcesses();
        Notifier = new FakeNotifier();
        var paths = _folder.Paths;
        State = new StateStore(paths.StateFile);
        Integrity = new IntegrityStore(paths.IntegrityFile, paths.Machine);
        var retry = new Retry(Time, Redactor, Log, attempts: 1);
        var disk = new FakeDisk();
        // As in the program, a session writes the WSL version it reads into the state file (the repeated probe and the gate read it from there).
        var sandbox = Sandboxes.New(Wsl, new FakeWslAccount(), new FakeWslSettingsFile(), Log, Redactor, Time, withProbeRenewal ? new StateMachineRecord(State, Time) : null);
        UbuntuImage? pinned = null;
        if (withDownloadableImage)
        {
            var bytes = Encoding.UTF8.GetBytes("ubuntu");
            pinned = new UbuntuImage("ubuntu-test.wsl", Convert.ToHexStringLower(SHA256.HashData(bytes)), new Uri("https://releases.ubuntu.com/noble/ubuntu-test.wsl"), bytes.Length);
            Downloads.Files[pinned.Address.ToString()] = bytes;
        }

        var image = new BaseImageBuilder(Downloads, Wsl, sandbox, paths, Log, retry, Integrity, disk, new ResidueRecorder(State), Time, pinned);
        var runners = new RunnerCache(Api, Downloads, paths, Log, retry, Integrity, new FakeProtectedStorage(), disk);
        Paths = paths;
        Installer = new FakeWslInstaller();
        // As in the program: the installer is wrapped in the one time limit of the update (the maintenance puts no second one around it).
        WslUpdate = withWslUpdate ? new WeeklyWslUpdate(new LimitedWslInstaller(Installer, WslLimits.Install, Time), Processes, () => UpdateSwitchedOff ? "the file no-wsl-update is in the data folder" : null, State, Time, Log, Redactor) : null;
        if (withProbeRenewal)
        {
            Directory.CreateDirectory(paths.BaseDirectory);
            File.WriteAllText(paths.BaseArchive, "base");
            Integrity.Record(paths.BaseArchive, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("base"))));
            Directory.CreateDirectory(paths.WslDistrosFolder);
            AcceptedProbe.In(State);
            Wsl.VersionText = AcceptedProbe.Version;
            Marker = new FakeProbeMarker();
            Locations = new FakeWritableLocations();
            Secrets = new FakeSecretStore();
            Secrets.Save(new SecretText("github_pat_11GOODGOODGOODGOODGOOD_abcdefghijklmnopqrstuvwxyz"));
            Probe = new IsolationProbe(Wsl, sandbox, paths, Integrity, Marker, Locations, new ResidueRecorder(State), Log, Redactor, Time);
            Gate = AcceptedProbe.Gate(State);
            var jobGate = new JobGate(Processes);
            Renewal = new ProbeRenewal(Probe, Wsl, State, () => AcceptedProbe.Recipe, new RoutingRemover(new LogConsoleUi(Log), Api, Secrets, Log, Redactor), new OwnerNotices(Notifier, State, Time), jobGate, Log, Redactor, Time);
        }

        Maintenance = new Maintenance(runners, image, Api, State, new JobGate(Processes), Notifier, Time, Log, Redactor, tokenCheckLimit, runnerDownloadLimit, imageBuildLimit, WslUpdate, Renewal, Gate);
    }

    /// <summary>The isolation probe the renewal repeats; null unless the rig was made with <c>withProbeRenewal</c>.</summary>
    public IsolationProbe? Probe { get; }

    public FakeProbeMarker? Marker { get; }

    public FakeWritableLocations? Locations { get; }

    public FakeSecretStore? Secrets { get; }

    public ProbeGate? Gate { get; }

    public ProbeRenewal? Renewal { get; }

    public FakeTimeProvider Time { get; }

    public Redactor Redactor { get; }

    public MemoryCiLog Log { get; }

    public FakeGitHubApi Api { get; }

    public FakeDownloads Downloads { get; }

    public FakeWsl Wsl { get; }

    public FakeProcesses Processes { get; }

    public FakeNotifier Notifier { get; }

    public StateStore State { get; }

    public IntegrityStore Integrity { get; }

    public LocalCiPaths Paths { get; }

    public Maintenance Maintenance { get; }

    public FakeWslInstaller Installer { get; }

    public WeeklyWslUpdate? WslUpdate { get; }

    /// <summary>True when the owner has put the file that switches the WSL update off.</summary>
    public bool UpdateSwitchedOff { get; set; }

    /// <summary>Marks everything as just done, so a test sets only the one thing it is about to be due.</summary>
    public void EverythingJustDone()
    {
        var now = Time.GetUtcNow();
        State.Update(s =>
        {
            s.LastTokenCheck = now;
            s.LastRunnerCheck = now;
            s.LastRunnerAttempt = now;
            s.LastBaseImageBuild = now;
            s.LastBaseImageAttempt = now;
            s.LastWslUpdateAttempt = now;
        });
    }

    public void Dispose() => _folder.Dispose();
}
