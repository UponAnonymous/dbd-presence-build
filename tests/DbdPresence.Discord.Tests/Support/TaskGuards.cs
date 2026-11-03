namespace DbdPresence.Discord.Tests.Support;

/// <summary>
/// Bounds how long a test waits for a task. The limit is wall-clock time and only matters when a test is
/// already failing (a hang); it never delays a passing test.
/// </summary>
internal static class TaskGuards
{
    private static readonly TimeSpan s_hangLimit = TimeSpan.FromSeconds(20);

    public static Task<T> Guard<T>(this Task<T> task) => task.WaitAsync(s_hangLimit);

    public static Task Guard(this Task task) => task.WaitAsync(s_hangLimit);

    public static Task<T> Guard<T>(this ValueTask<T> task) => task.AsTask().WaitAsync(s_hangLimit);

    public static Task Guard(this ValueTask task) => task.AsTask().WaitAsync(s_hangLimit);
}
