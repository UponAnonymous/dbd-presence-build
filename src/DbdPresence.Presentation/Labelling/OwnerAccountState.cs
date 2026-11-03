namespace DbdPresence.Presentation.Labelling;

/// <summary>
/// Whether the signed-in account is the owner's own game account (<c>user.rosterReporter</c> of <c>GET /v1/me</c>, the flag of owner decision D35). The Study
/// page exists only while this is known to be true; signed out, another person's account or an unknown answer all mean no page.
/// </summary>
public interface IOwnerAccountState
{
    /// <summary>Raised (on any thread) after <see cref="IsOwner"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gets a value indicating whether this is the owner's own game account: true, false, or null when the service has not said.</summary>
    bool? IsOwner { get; }

    /// <summary>Takes an answer: the service said whether this account is the owner's own game account.</summary>
    /// <param name="isOwner">The answer.</param>
    void Note(bool isOwner);

    /// <summary>Forgets the answer (signed out, or the account changed).</summary>
    void Forget();
}

/// <summary>The one holder of the owner flag. It makes no call of its own: the normal account refresh reports into it.</summary>
public sealed class OwnerAccountState : IOwnerAccountState
{
    private readonly Lock _gate = new();
    private bool? _isOwner;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool? IsOwner
    {
        get
        {
            lock (_gate)
            {
                return _isOwner;
            }
        }
    }

    /// <inheritdoc />
    public void Note(bool isOwner) => Set(isOwner);

    /// <inheritdoc />
    public void Forget() => Set(null);

    private void Set(bool? value)
    {
        lock (_gate)
        {
            if (_isOwner == value)
            {
                return;
            }

            _isOwner = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
