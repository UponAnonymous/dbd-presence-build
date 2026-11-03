namespace DbdPresence.LocalCi;

/// <summary>
/// The one step that is run as root inside a new job machine before the runner exists (local CI round 3, red-team H1): user namespaces are closed (<c>user.max_user_namespaces=0</c>). A job that
/// gains root inside its Linux still shares the machine's kernel; making a user namespace is how an ordinary user reaches much of the kernel's code (network filter rules, mounts) that is only meant
/// for root, so closing it before the job starts removes that way and costs the workflows nothing they use (they build, test and run Node, Python and .NET; none makes namespaces). The value is a
/// setting of the running kernel, so it holds until the machine is stopped, which is after the job; the runner user cannot change it (it is not root, has no sudo and, since
/// <c>setpriv --no-new-privs</c>, cannot gain privileges through a program). The isolation check that follows proves the result from inside (<c>no-user-namespaces</c>).
/// </summary>
public static class WslHardening
{
    /// <summary>
    /// Run as root. Writes <c>0</c> to the kernel setting and reads it back; exits with 1 and says so on standard error when it is not 0. The line <c>R=""</c> is the root folder prefix tests replace
    /// (see <see cref="WslPreflight.Script"/>).
    /// </summary>
    public const string Script = """
        set -u
        R=""
        limit="$R/proc/sys/user/max_user_namespaces"
        if [ -e "$limit" ]; then printf '0\n' > "$limit" 2>/dev/null; fi
        now=""
        { read -r now < "$limit"; } 2>/dev/null
        if [ "$now" != "0" ]; then echo "user namespaces could not be closed (limit is ${now:-unknown})" >&2; exit 1; fi
        echo "user namespaces are closed"
        """;
}
