namespace DbdPresence.LocalCi;

/// <summary>What the token check learns about the repository.</summary>
/// <param name="DefaultBranch">The repository's default branch.</param>
/// <param name="TokenExpiresAt">When the token ends, if GitHub said.</param>
/// <param name="IsPrivate">True when GitHub says the repository is private, false when it says it is not, null when its answer does not say (local CI round 3, red-team M5: only a private repository is served).</param>
public sealed record RepoInfo(string DefaultBranch, DateTimeOffset? TokenExpiresAt, bool? IsPrivate);

/// <summary>A just-in-time runner that GitHub has registered; the configuration is a secret.</summary>
public sealed class JitRunner
{
    public JitRunner(long runnerId, string name, SecretText encodedConfig)
    {
        RunnerId = runnerId;
        Name = name;
        EncodedConfig = encodedConfig;
    }

    public long RunnerId { get; }

    public string Name { get; }

    public SecretText EncodedConfig { get; }

    public override string ToString() => $"runner {Name} ({RunnerId})";
}

public sealed record RunnerInfo(long Id, string Name, string Status, bool Busy);

/// <summary>
/// The newest actions/runner release: its version, release notes and the download address of the Linux package, with the SHA-256 GitHub itself keeps for the file
/// (the <c>digest</c> of the release asset; null when GitHub gave none). The notes carry a second checksum; the two must agree. Only the Linux package is used (decision D41).
/// </summary>
public sealed record RunnerRelease(string Version, string Notes, Uri LinuxPackage, string? LinuxDigest = null);

public sealed record WorkflowRunInfo(long Id, string Status, string? Conclusion, DateTimeOffset CreatedAt);

/// <summary>One job of a workflow run: its state, the runner that took it (null when none did), how many steps it started and the labels it asked for (<c>runs-on</c>).</summary>
public sealed record WorkflowJobInfo(long Id, string Name, string Status, string? Conclusion, string? RunnerName, int StepCount, IReadOnlyList<string>? Labels = null);

/// <summary>The GitHub REST calls the program makes, for one repository, with the owner's token.</summary>
public interface IGitHubApi
{
    /// <summary>One cheap call that proves the token works and reads its expiry from the response.</summary>
    Task<RepoInfo> GetRepositoryAsync(CancellationToken cancellationToken);

    Task<RunnerRelease> GetLatestRunnerReleaseAsync(CancellationToken cancellationToken);

    Task<JitRunner> GenerateJitConfigAsync(string name, IReadOnlyList<string> labels, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync(CancellationToken cancellationToken);

    /// <summary>The value of a repository Actions variable, or null when it does not exist.</summary>
    Task<string?> GetVariableAsync(string name, CancellationToken cancellationToken);

    /// <summary>Removes a runner registration. A runner that is already gone is not an error.</summary>
    Task DeleteRunnerAsync(long runnerId, CancellationToken cancellationToken);

    /// <summary>Creates the variable or, when it exists, updates it.</summary>
    Task SetVariableAsync(string name, string value, CancellationToken cancellationToken);

    /// <summary>Deletes the variable. One that is already gone is not an error.</summary>
    Task DeleteVariableAsync(string name, CancellationToken cancellationToken);

    /// <summary>Starts a workflow by file name on a branch; returns the run id when GitHub reports it.</summary>
    Task<long?> DispatchWorkflowAsync(string workflowFile, string branch, CancellationToken cancellationToken);

    /// <summary>The newest runs of a workflow (at most 10), of one event (<c>push</c>, <c>workflow_dispatch</c>) or of every event when <paramref name="eventName"/> is null.</summary>
    Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(string workflowFile, string? eventName, CancellationToken cancellationToken);

    Task<WorkflowRunInfo> GetWorkflowRunAsync(long runId, CancellationToken cancellationToken);

    /// <summary>Cancels a run (the self-test does this when nothing picked it up).</summary>
    Task CancelWorkflowRunAsync(long runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowJobInfo>> ListWorkflowJobsAsync(long runId, CancellationToken cancellationToken);
}

/// <summary>A GitHub call that failed. <see cref="IsTransient"/> says whether trying again later can help.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, int statusCode, bool isTransient, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status, or 0 when there was no answer.</summary>
    public int StatusCode { get; }

    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// True when the token is not a fine-grained token: GitHub answered with an <c>X-OAuth-Scopes</c> header, which only classic tokens and OAuth tokens get. Such a token reaches every repository of the
    /// account, so the program refuses it (local CI round 3, red-team L9).
    /// </summary>
    public bool IsClassicToken { get; init; }

    public bool IsAuthenticationFailure => StatusCode is 401 or 403 && !IsTransient;
}

/// <summary>Release notes or a checksum list did not say what a safe download needs.</summary>
public sealed class DownloadVerificationException : Exception
{
    public DownloadVerificationException(string message)
        : base(message)
    {
    }
}
