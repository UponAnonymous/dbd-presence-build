using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DbdPresence.LocalCi.Windows;

/// <summary>The dedicated account as the launcher needs it: its name, its SID (for the private desktop's permissions) and its password. The password never prints (<see cref="SecretText"/> hides itself).</summary>
internal sealed record AccountCredentials(string UserName, string Sid, SecretText Password);

/// <summary>
/// Starts one program as the dedicated account and holds it (security review 2026-10-30, H2; local CI round 2, red-team F2, F5). The program is started with <c>CreateProcessWithLogonW</c>, suspended,
/// with <c>lpDesktop</c> naming a private, non-interactive window station and desktop; it is put into a Job Object (with limits) before it runs a single instruction; then it is resumed. When it
/// ends, or when the token is cancelled, the whole Job Object is ended and checked empty. If any isolation step fails, the program never runs. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
/// <remarks>
/// Why CreateProcessWithLogonW and not LogonUser + CreateProcessAsUser: CreateProcessAsUser "may require the SE_ASSIGNPRIMARYTOKEN_NAME privilege" which an elevated administrator does not have, and the
/// documentation says "If this function fails with ERROR_PRIVILEGE_NOT_HELD (1314), use the CreateProcessWithLogonW function instead. CreateProcessWithLogonW requires no special privileges" (CreateProcessAsUserW
/// documentation). CreateProcessWithLogonW takes the window station and desktop from <c>STARTUPINFO.lpDesktop</c>; "The application must add permission for the specified user account to the specified window
/// station and desktop" (CreateProcessWithLogonW documentation), which <see cref="PrivateDesktopSecurity"/> does when the station is created. Both pages read 2026-10-29. A process that is already in a Job Object
/// can join another only as a nested job, and a nested job cannot have user-interface limits (AssignProcessToJobObject documentation, read 2026-10-30; Windows API review 2026-10-30, risk 1): the limits are
/// added only when <c>IsProcessInJob</c> says the new program is in none, and a program that cannot be put in the job is ended before it ran (it never runs outside a Job Object).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class LogonJob
{
    private static readonly TimeSpan s_endLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_pumpLimit = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(AccountCommand command, AccountCredentials account, ICiLog log, Redactor redactor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(account);
        var program = ProgramPaths.Require(command.FileName);
        var commandLine = CommandLineQuoting.Join(program, command.Arguments);
        if (commandLine.Length >= CommandLineQuoting.LogonCommandLineLimit)
        {
            throw new InvalidOperationException($"The command line is {commandLine.Length} characters; Windows accepts fewer than {CommandLineQuoting.LogonCommandLineLimit} for another user.");
        }

        using var desktop = PrivateDesktop.Create(account.Sid);
        using var job = JobObject.Create();
        using var standardOutput = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var standardError = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var standardInput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var pumps = new PumpSet(cancellationToken);

        var started = default(ProcessInformation);
        var running = false;
        try
        {
            started = StartSuspended(account, program, commandLine, desktop.Path, standardInput, standardOutput, standardError);
            running = true;

            // The child holds its own copies of the pipe ends now. Closing ours lets reading end when it ends.
            standardOutput.DisposeLocalCopyOfClientHandle();
            standardError.DisposeLocalCopyOfClientHandle();
            standardInput.DisposeLocalCopyOfClientHandle();

            AssignOrEnd(job, started, log);
            if (NativeMethods.ResumeThread(started.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not let the program run.");
            }

            pumps.Start(ct => AccountStreams.PumpInputAsync(standardInput, command.StandardInput, ct));
            pumps.Start(ct => AccountStreams.PumpOutputAsync(standardOutput, command.RawOutput, command.OutputEncoding, command.OnLine, redactor, ct));
            pumps.Start(ct => AccountStreams.PumpOutputAsync(standardError, null, command.OutputEncoding, command.OnLine, redactor, ct));
            try
            {
                await WaitForExitAsync(started.Process, pumps.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && pumps.Failure is { } failure)
            {
                throw new IOException($"Handling the program's input or output failed, so it was ended: {failure.Message}", failure);
            }

            if (!NativeMethods.GetExitCodeProcess(started.Process, out var code))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows would not say how the program ended.");
            }

            // Whatever the program printed before it ended is still in the pipes; read it to the end (bounded), then see whether a pump failed.
            _ = await pumps.WhenAllAsync(s_pumpLimit, TimeProvider.System).ConfigureAwait(false);
            if (pumps.Failure is { } late)
            {
                throw new IOException($"Handling the program's input or output failed: {late.Message}", late);
            }

            return unchecked((int)code);
        }
        finally
        {
            try
            {
                if (running)
                {
                    await job.EndAllAsync(s_endLimit).ConfigureAwait(false);
                }
            }
            finally
            {
                if (running)
                {
                    _ = NativeMethods.CloseHandle(started.Thread);
                    _ = NativeMethods.CloseHandle(started.Process);
                }

                _ = await pumps.WhenAllAsync(s_pumpLimit, TimeProvider.System).ConfigureAwait(false);
                log.Info("The program started as the account, and everything it started, have ended.");
            }
        }
    }

    private static ProcessInformation StartSuspended(
        AccountCredentials account,
        string program,
        string commandLine,
        string desktopPath,
        AnonymousPipeServerStream standardInput,
        AnonymousPipeServerStream standardOutput,
        AnonymousPipeServerStream standardError)
    {
        var password = Marshal.StringToCoTaskMemUni(account.Password.Reveal());
        var commandLinePointer = Marshal.StringToCoTaskMemUni(commandLine);
        var desktopPointer = Marshal.StringToCoTaskMemUni(desktopPath);
        try
        {
            var startup = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfo>(),
                Desktop = desktopPointer,
                Flags = NativeMethods.StartUseStdHandles | NativeMethods.StartUseShowWindow,
                ShowWindow = 0,
                StandardInput = standardInput.ClientSafePipeHandle.DangerousGetHandle(),
                StandardOutput = standardOutput.ClientSafePipeHandle.DangerousGetHandle(),
                StandardError = standardError.ClientSafePipeHandle.DangerousGetHandle(),
            };
            var flags = NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment | NativeMethods.CreateNoWindow;

            // lpEnvironment is NULL: the program gets the environment of the account's own profile, not the owner's. The folder it starts in is the Windows system folder, which every user can read.
            if (!NativeMethods.CreateProcessWithLogon(
                    account.UserName, Environment.MachineName, password, NativeMethods.LogonWithProfile, program, commandLinePointer, flags, nint.Zero, Environment.SystemDirectory, ref startup, out var information))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, $"Windows would not start {Path.GetFileName(program)} as {account.UserName}: {LogonErrors.Describe(error, account.UserName)}");
            }

            return information;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(password);
            Marshal.FreeCoTaskMem(commandLinePointer);
            Marshal.FreeCoTaskMem(desktopPointer);
        }
    }

    /// <summary>
    /// The program is held back until it is inside the Job Object; if that fails it is ended before it ran anything (it never runs outside a Job Object). The user-interface limits are added only
    /// when the program is in no other job; when it is in one (or Windows will not say), they are left off, a distinct line says so, and the private window station is the isolation.
    /// </summary>
    private static void AssignOrEnd(JobObject job, ProcessInformation started, ICiLog log)
    {
        var inAnotherJob = JobObject.IsInAnyJob(started.Process);
        if (inAnotherJob is null)
        {
            log.Warn("Windows would not say whether the program is already in a Job Object. It is treated as being in one: the user-interface limits are left off, the private window station and the other limits stay.");
        }
        else if (inAnotherJob is true)
        {
            log.Warn("The program is already in another Job Object (security software and some Windows features put new programs in one). A job inside a job cannot have user-interface limits, so they are left off; the private window station, the end-all-on-close, the program count and the memory limit stay.");
        }

        try
        {
            if (inAnotherJob is false)
            {
                job.RestrictUserInterface();
            }

            job.Assign(started.Process);
        }
        catch (Win32Exception ex)
        {
            _ = NativeMethods.TerminateProcess(started.Process, 1);
            var reason = inAnotherJob is false
                ? "Windows would not put the program in its Job Object, so it was not started."
                : "Windows would not put the program in this program's Job Object because it is already in another one, so it was not started (it never runs outside a Job Object).";
            throw new Win32Exception(ex.NativeErrorCode, reason + " " + ex.Message);
        }
    }

    private static async Task WaitForExitAsync(nint process, CancellationToken cancellationToken)
    {
        using var handle = new ProcessWaitHandle(process);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = ThreadPool.RegisterWaitForSingleObject(handle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), finished, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await using (cancellationToken.Register(() => finished.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
            {
                await finished.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _ = registered.Unregister(null);
        }
    }

    /// <summary>Lets the thread pool wait on a process handle without closing it when the wait ends.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(nint process)
        {
            SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
        }
    }
}

/// <summary>Runs programs as the dedicated account through <see cref="LogonJob"/> (the only place that does).</summary>
[SupportedOSPlatform("windows")]
internal sealed class LogonAccountProcess : IAccountProcess
{
    private readonly WindowsWslAccount _account;
    private readonly ICiLog _log;
    private readonly Redactor _redactor;

    public LogonAccountProcess(WindowsWslAccount account, ICiLog log, Redactor redactor)
    {
        _account = account;
        _log = log;
        _redactor = redactor;
    }

    public Task<int> RunAsync(AccountCommand command, CancellationToken cancellationToken) =>
        LogonJob.RunAsync(command, _account.Credentials(), _log, _redactor, cancellationToken);
}
