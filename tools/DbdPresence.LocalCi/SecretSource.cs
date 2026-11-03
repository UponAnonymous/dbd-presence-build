namespace DbdPresence.LocalCi;

/// <summary>
/// The secret a client uses (the GitHub token, the password of the dedicated Windows account): the one being checked during setup, otherwise the saved one. Every secret that is handed out is
/// registered with the <see cref="Redactor"/> first, so it can never reach a log line or a message (security review 2026-10-30, low findings; local CI round 2, QA M2/M3).
/// </summary>
public sealed class SecretSource
{
    private readonly ISecretStore _store;
    private readonly Redactor? _redactor;
    private SecretText? _candidate;

    public SecretSource(ISecretStore store, Redactor? redactor = null)
    {
        _store = store;
        _redactor = redactor;
    }

    public SecretText? Current()
    {
        var token = _candidate ?? _store.Load();
        if (token is not null)
        {
            _redactor?.Register(token);
        }

        return token;
    }

    public void TryCandidate(SecretText candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _redactor?.Register(candidate);
        _candidate = candidate;
    }

    public void ClearCandidate() => _candidate = null;
}
