namespace DbdPresence.LocalCi.Tests;

/// <summary>A set-up wizard built from fakes with every part within reach of a test; a test changes only what it is about.</summary>
internal sealed class SetupRig : IDisposable
{
    public const string GoodToken = "github_pat_11GOODGOODGOODGOODGOOD_abcdefghijklmnopqrstuvwxyz";

    private readonly TempFolder _folder = new();

    public SetupRig(IEnumerable<string>? secrets = null, IEnumerable<bool>? answers = null, IEnumerable<string>? typed = null)
    {
        Redactor = new Redactor();
        Log = new MemoryCiLog(Redactor);
        Console = new FakeConsole(secrets ?? [GoodToken], answers, typed);
        Machine = new FakeMachine();
        Wsl = new FakeWsl();
        Api = new FakeGitHubApi();
        Store = new FakeSecretStore();
        Scheduler = new FakeScheduler();
        Installer = new FakeInstaller();
        Storage = new FakeProtectedStorage();
        Account = new FakeWslAccount();
        Settings = new FakeWslSettingsFile();
        WslInstaller = new FakeWslInstaller();
        Wizard = SetupTests.NewWizard(_folder, Console, Machine, Wsl, Api, Store, Scheduler, Redactor, Log, out var state, Installer, Storage, Account, Settings, WslInstaller);
        State = state;
        Paths = _folder.Paths;
    }

    public SetupWizard Wizard { get; }

    public Redactor Redactor { get; }

    public MemoryCiLog Log { get; }

    public FakeConsole Console { get; }

    public FakeMachine Machine { get; }

    public FakeWsl Wsl { get; }

    public FakeGitHubApi Api { get; }

    public FakeSecretStore Store { get; }

    public FakeScheduler Scheduler { get; }

    public FakeInstaller Installer { get; }

    public FakeProtectedStorage Storage { get; }

    public FakeWslAccount Account { get; }

    public FakeWslSettingsFile Settings { get; }

    public FakeWslInstaller WslInstaller { get; }

    public StateStore State { get; }

    public LocalCiPaths Paths { get; }

    /// <summary>A PC where set-up has run before: the loop is running and the variable sends checks to it.</summary>
    public SetupRig WithAWorkingEarlierSetup()
    {
        Scheduler.TaskExists = true;
        Scheduler.Running = true;
        Api.Variables["LINUX_RUNNER"] = LocalCiDefaults.LinuxLabel;
        Installer.IsInstalled = true;
        return this;
    }

    public void Dispose() => _folder.Dispose();
}
