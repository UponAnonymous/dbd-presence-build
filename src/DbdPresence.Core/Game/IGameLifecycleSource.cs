namespace DbdPresence.Core.Game;

/// <summary>Provides the confirmed game lifecycle and notifies when it changes.</summary>
public interface IGameLifecycleSource
{
    /// <summary>Raised when the confirmed lifecycle changes (on a background thread).</summary>
    event EventHandler<GameLifecycle>? LifecycleChanged;

    /// <summary>Gets the current confirmed lifecycle.</summary>
    GameLifecycle Current { get; }
}
