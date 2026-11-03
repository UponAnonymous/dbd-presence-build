namespace DbdPresence.LocalCi;

/// <summary>What GitHub says about the repository's visibility.</summary>
public enum RepositoryPrivacy
{
    /// <summary>GitHub says the repository is private.</summary>
    Private,

    /// <summary>GitHub says it is not private (it is public or internal).</summary>
    NotPrivate,

    /// <summary>GitHub could not be asked, or its answer did not say.</summary>
    Unknown,
}

/// <summary>The answer and, in plain words, what it means for starting a runner.</summary>
/// <param name="Privacy">What GitHub said.</param>
/// <param name="Reason">Why no runner may start (empty when the repository is private).</param>
public sealed record PrivacyVerdict(RepositoryPrivacy Privacy, string Reason)
{
    /// <summary>True only for a repository GitHub says is private: the program serves nothing else.</summary>
    public bool MayServe => Privacy == RepositoryPrivacy.Private;
}

/// <summary>
/// Only a private repository is served (local CI round 3, red-team M5). Jobs of a public repository can be started by anybody who opens a pull request, so a runner on the owner's PC would run strangers'
/// code. The program reads <c>private</c> from the repository call every time a runner is about to be registered and now and then while one waits, and refuses on anything but a clear "private": a
/// repository that is not private and an answer that does not say are the same to it (fail closed).
/// </summary>
public sealed class RepositoryGuard
{
    private readonly IGitHubApi _api;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;

    public RepositoryGuard(IGitHubApi api, StateStore state, TimeProvider time, ICiLog log, Redactor redactor)
    {
        _api = api;
        _state = state;
        _time = time;
        _log = log;
        _redactor = redactor;
    }

    /// <summary>The words that say a repository is not served; used by the status, set-up and the log.</summary>
    public static string NotPrivateSentence => $"The repository {LocalCiDefaults.RepositoryOwner}/{LocalCiDefaults.RepositoryName} is not private. Anybody who can open a pull request could then run code on this PC, so no runner is started. Make the repository private (Settings, General, Danger Zone, Change visibility), then it starts again by itself.";

    /// <summary>The words for an answer that does not say.</summary>
    public static string UnknownSentence => $"GitHub did not say whether the repository {LocalCiDefaults.RepositoryOwner}/{LocalCiDefaults.RepositoryName} is private, so no runner is started until it does.";

    /// <summary>Judges what the repository call said.</summary>
    public static PrivacyVerdict Judge(bool? isPrivate) => isPrivate switch
    {
        true => new PrivacyVerdict(RepositoryPrivacy.Private, string.Empty),
        false => new PrivacyVerdict(RepositoryPrivacy.NotPrivate, NotPrivateSentence),
        null => new PrivacyVerdict(RepositoryPrivacy.Unknown, UnknownSentence),
    };

    /// <summary>
    /// Asks GitHub and remembers the answer in the state file (for the status). A call that fails is <see cref="RepositoryPrivacy.Unknown"/> with the reason, never an exception: the caller decides what
    /// that means (no runner starts on it; a runner that already waits is left alone). A stop of the loop itself passes through.
    /// </summary>
    public async Task<PrivacyVerdict> AskAsync(CancellationToken cancellationToken)
    {
        RepoInfo repo;
        try
        {
            repo = await _api.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Whatever stops the question (a refusal, a lost connection, a damaged answer) means "not known"; it is logged and the caller fails closed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warn("Could not ask GitHub whether the repository is private: " + _redactor.Describe(ex));
            return new PrivacyVerdict(RepositoryPrivacy.Unknown, "GitHub could not be asked whether the repository is private (" + _redactor.Redact(ex.Message) + "), so no runner is started until it can.");
        }

        Remember(repo.IsPrivate);
        return Judge(repo.IsPrivate);
    }

    /// <summary>
    /// Remembers what an answer from elsewhere (the token check) said, so the status is current. A state file that cannot be written is logged and does not change the answer: the status then shows an
    /// older one, and the verdict itself is still what GitHub just said.
    /// </summary>
    public void Remember(bool? isPrivate)
    {
        var now = _time.GetUtcNow();
        try
        {
            _state.Update(s =>
            {
                s.RepositoryPrivate = isPrivate;
                s.RepositoryCheckedAt = now;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not write down what GitHub said about the repository: " + _redactor.Describe(ex));
        }
    }
}
