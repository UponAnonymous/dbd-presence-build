using System.Runtime.Versioning;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// The programs the elevated loop may start, each by its full path (security review 2026-10-30, M3). A bare name such as <c>wsl.exe</c> is looked for in the current folder and the search
/// path, which an ordinary program can influence; a full path built from the Windows folder cannot be redirected. Every start goes through <see cref="Require"/>, which throws for anything else,
/// and the architecture test checks that no code starts a process without calling it. The list itself is <see cref="ApprovedPrograms"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProgramPaths
{
    private static readonly ApprovedPrograms s_programs = new(Environment.SystemDirectory);

    public static string Wsl => s_programs.Wsl;

    public static string Schtasks => s_programs.Schtasks;

    public static string Taskkill => s_programs.Taskkill;

    /// <summary>Adds a program whose full path was found and checked elsewhere (this program's installed copy).</summary>
    public static void Approve(string fullPath) => s_programs.Approve(fullPath);

    /// <summary>Returns the path when it is a full path of an approved program; throws otherwise.</summary>
    public static string Require(string fileName) => s_programs.Require(fileName);
}
