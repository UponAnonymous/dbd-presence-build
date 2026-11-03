namespace DbdPresence.Core.Online;

/// <summary>A secret could not be stored, read or removed.</summary>
public sealed class SecretStoreException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    public SecretStoreException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    /// <param name="message">What failed (never the secret).</param>
    public SecretStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreException"/> class.</summary>
    /// <param name="message">What failed (never the secret).</param>
    /// <param name="innerException">The underlying failure.</param>
    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Named secrets protected for the current Windows user (DPAPI; ADR-0011): the refresh and access tokens, the licence and the
/// newest server time seen. Names are lower-case letters, digits and hyphens.
/// </summary>
public interface ISecretStore
{
    /// <summary>Reads a secret, or null when there is none.</summary>
    /// <param name="name">The secret's name.</param>
    /// <returns>The value, or null.</returns>
    /// <exception cref="SecretStoreException">The stored value cannot be read (corrupt, or protected for another user).</exception>
    string? Read(string name);

    /// <summary>Stores a secret, replacing any previous value atomically.</summary>
    /// <param name="name">The secret's name.</param>
    /// <param name="value">The value; never logged.</param>
    /// <exception cref="SecretStoreException">The value could not be stored.</exception>
    void Write(string name, string value);

    /// <summary>Removes a secret. Succeeds when there is none.</summary>
    /// <param name="name">The secret's name.</param>
    /// <exception cref="SecretStoreException">The value exists but could not be removed.</exception>
    void Delete(string name);
}

/// <summary>Opens the system browser at Discord's sign-in page (ADR-0016: the only page the app opens).</summary>
public interface IBrowserLauncher
{
    /// <summary>Opens <paramref name="url"/>; refuses anything that is not Discord's OAuth authorize page.</summary>
    /// <param name="url">The authorize URL.</param>
    /// <returns>True when the browser was started.</returns>
    bool TryOpen(string url);
}

/// <summary>Diagnostics sink for the online layer. Messages never contain tokens, codes or keys.</summary>
public interface IOnlineLog
{
    /// <summary>Records a noteworthy event.</summary>
    /// <param name="message">A short message without secrets.</param>
    void Warning(string message);
}
