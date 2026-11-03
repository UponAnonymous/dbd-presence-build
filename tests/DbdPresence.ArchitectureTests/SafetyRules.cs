using System.Reflection.Metadata;

namespace DbdPresence.ArchitectureTests;

/// <summary>
/// The SAFETY_AND_PRIVACY §2 boundary as pure rules over an inspected assembly. Each rule returns its
/// violations (empty = compliant). Product assemblies must yield none; <see cref="InspectorSelfTests"/>
/// proves every rule reports the deliberate violations in this test assembly, so a pass is never vacuous.
/// Extending an allow-list is a reviewed change (QUALITY_GATES U8).
/// </summary>
internal static class SafetyRules
{
    public const int ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Win32 entry points product code may declare (SAFETY §2.1, ARCHITECTURE §7.1).</summary>
    public static readonly IReadOnlySet<string> AllowedPInvokes = new HashSet<string>(StringComparer.Ordinal)
    {
        "OpenProcess", "CloseHandle", "GetProcessTimes", "QueryFullProcessImageNameW",
        "EnumWindows", "GetWindowThreadProcessId", "IsWindowVisible", "IsIconic", "GetWindowRect",
        "GetClientRect", "ClientToScreen", "DwmGetWindowAttribute", "GetForegroundWindow",
        "MonitorFromWindow", "GetDpiForWindow",
        "DwmSetWindowAttribute",
        "D3D11CreateDevice", "CreateDirect3D11DeviceFromDXGIDevice",
        // C3 (ADR-0003 addendum): the monitor rectangle of the game window, to tell windowed from borderless/fullscreen. Read-only.
        "GetMonitorInfoW",
    };

    /// <summary>
    /// The only <c>[ComImport]</c> types product code may declare, each with its justification (C3, ADR-0003 addendum).
    /// Windows.Graphics.Capture can create a capture item for a window only through <c>IGraphicsCaptureItemInterop</c>; the classic
    /// interface is used because source-generated COM would emit function-pointer calls, which <see cref="DynamicCode"/> forbids.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedComImports = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DbdPresence.Platform.Windows.Capture.CaptureInterop+IGraphicsCaptureItemInterop"] =
            "CreateForWindow: the OS capture API for one window; the window handle is not used for anything else",
    };

    public static IReadOnlyList<string> ComImports(AssemblyInspector assembly) =>
    [
        .. assembly.ComImportTypes
            .Where(t => !AllowedComImports.ContainsKey(t))
            .Select(t => $"[ComImport] type {t} is not on the allow-list (add a named, justified entry in review)"),
    ];

    /// <summary>
    /// Entry points that change window state, mapped to the one type allowed to declare them. That type checks the
    /// handle belongs to this process (SAFETY §2.2), so restricting the declaration restricts every caller.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OwnerRestrictedPInvokes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DwmSetWindowAttribute"] = "DbdPresence.Platform.Windows.Ui.OwnWindowChrome",
    };

    /// <summary>Native modules product P/Invokes may target (compared without extension, case-insensitive).</summary>
    public static readonly IReadOnlySet<string> AllowedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "user32", "dwmapi", "d3d11",
    };

    /// <summary>Always-forbidden entry points (also caught by the allow-list; named for clear failures).</summary>
    public static readonly IReadOnlySet<string> BannedPInvokes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ReadProcessMemory", "WriteProcessMemory", "VirtualAllocEx", "VirtualProtectEx", "CreateRemoteThread",
        "NtCreateThreadEx", "NtReadVirtualMemory", "NtWriteVirtualMemory", "SetWindowsHookExW", "SetWindowsHookExA",
        "SetWinEventHook", "DebugActiveProcess", "SendInput", "keybd_event", "mouse_event", "PostMessageW",
        "PostMessageA", "SendMessageW", "SendMessageA", "AttachThreadInput", "OpenThread", "SuspendThread",
        "LoadLibraryW", "LoadLibraryA", "LoadLibraryExW", "GetProcAddress",
    };

    /// <summary>The only <c>System.Diagnostics.Process</c> members product code may use (others open handles).</summary>
    public static readonly IReadOnlySet<string> AllowedProcessMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetProcesses", "GetProcessesByName", "get_ProcessName", "get_Id", "Dispose", "Start",
    };

    /// <summary>Members that load or call code dynamically and could bypass the P/Invoke rules.</summary>
    public static readonly IReadOnlySet<string> BannedMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Runtime.InteropServices.NativeLibrary::Load",
        "System.Runtime.InteropServices.NativeLibrary::TryLoad",
        "System.Runtime.InteropServices.NativeLibrary::GetExport",
        "System.Runtime.InteropServices.NativeLibrary::TryGetExport",
        "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer",
        "System.Runtime.InteropServices.Marshal::GetFunctionPointerForDelegate",
        "System.Reflection.Assembly::Load",
        "System.Reflection.Assembly::LoadFrom",
        "System.Reflection.Assembly::LoadFile",
        "System.Reflection.Assembly::UnsafeLoadFrom",
        "System.Reflection.MethodBase::Invoke",
        "System.Type::InvokeMember",
        "System.Type::GetType",
        "System.Activator::CreateInstance",

        // Late-bound and by-name reflection (security review 2026-10-25, I1). A call through these hides its target from the IL scan,
        // so a banned API could be reached by a string. MethodBase::Invoke above does not cover the overrides that
        // ConstructorInfo declares itself, so it is named here.
        "System.Reflection.ConstructorInfo::Invoke",
        "System.Reflection.FieldInfo::GetValue",
        "System.Reflection.FieldInfo::SetValue",
        "System.Reflection.FieldInfo::GetValueDirect",
        "System.Reflection.FieldInfo::SetValueDirect",
        "System.Reflection.PropertyInfo::GetValue",
        "System.Reflection.PropertyInfo::SetValue",
        "System.Reflection.Assembly::GetType",
        "System.Reflection.Assembly::CreateInstance",
        "System.Reflection.Assembly::LoadModule",
        "System.Type::GetTypeFromProgID",
        "System.Type::GetTypeFromCLSID",
        "System.Activator::CreateInstanceFrom",
        "System.Activator::CreateComInstanceFrom",
        "System.AppDomain::Load",
        "System.AppDomain::CreateInstance",
        "System.AppDomain::CreateInstanceFrom",
        "System.AppDomain::CreateInstanceAndUnwrap",
        "System.AppDomain::CreateInstanceFromAndUnwrap",
        "System.AppDomain::ExecuteAssembly",
        "System.AppDomain::ExecuteAssemblyByName",
        "System.Runtime.InteropServices.Marshal::GetActiveObject",
        "System.Runtime.InteropServices.Marshal::BindToMoniker",
        "System.Runtime.InteropServices.Marshal::GetTypedObjectForIUnknown",
        "System.Delegate::DynamicInvoke",
        "System.Delegate::CreateDelegate",
        "System.Reflection.MethodInfo::CreateDelegate",
        "System.Linq.Expressions.LambdaExpression::Compile",
        "System.Linq.Expressions.Expression`1::Compile",

        // Custom certificate validation is banned everywhere, including DbdPresence.Online (ADR-0016): TLS uses the system store only.
        "System.Net.Security.SslClientAuthenticationOptions::set_RemoteCertificateValidationCallback",
        "System.Net.Security.SslServerAuthenticationOptions::set_RemoteCertificateValidationCallback",
        "System.Net.Http.HttpClientHandler::set_ServerCertificateCustomValidationCallback",
        "System.Net.Http.HttpClientHandler::get_DangerousAcceptAnyServerCertificateValidator",
        "System.Net.ServicePointManager::set_ServerCertificateValidationCallback",
    };

    /// <summary>Banned member prefixes: every <c>AssemblyLoadContext.Load*</c> overload (assembly and native loads).</summary>
    public static readonly IReadOnlyList<string> BannedMemberPrefixes =
    [
        "System.Runtime.Loader.AssemblyLoadContext::Load",
        // GetRuntimeField/GetRuntimeMethod/... look a member up by name (I1).
        "System.Reflection.RuntimeReflectionExtensions::",
    ];

    /// <summary>
    /// Types product code must not reference at all (entries ending in "." are namespace prefixes, others exact names):
    /// runtime code generation, and the WinRT launcher that would open arbitrary URIs or files through the shell (ADR-0016).
    /// </summary>
    public static readonly IReadOnlyList<string> BannedProductTypes =
    [
        "System.Reflection.Emit.",
        "Windows.System.Launcher",
        "Windows.System.LauncherOptions",

        // Process-inspecting APIs that sit outside System.Diagnostics.Process (I1). The reference allow-list admits "System." and
        // "Windows.*" wholesale, so these are named. SAFETY §2.1 allows enumeration by name/PID and the two OpenProcess calls only.
        "System.Management.",
        "Windows.System.Diagnostics.",
        "Windows.System.AppDiagnosticInfo",
        "Windows.System.AppResourceGroupInfo",
        "Windows.System.ProcessLauncher",
        "System.Diagnostics.ProcessModule",
        "System.Diagnostics.ProcessModuleCollection",
        "System.Diagnostics.ProcessThread",
        "System.Diagnostics.ProcessThreadCollection",
        "System.Diagnostics.PerformanceCounter",
        "System.Diagnostics.PerformanceCounterCategory",
        "System.Diagnostics.Eventing.Reader.",
        "System.Net.Security.RemoteCertificateValidationCallback",
        "System.Net.Security.LocalCertificateSelectionCallback",

        // Run-time XAML parsing turns text into live objects (threat-model review TM-11); the product loads only compiled XAML.
        "System.Windows.Markup.XamlReader",
        "System.Xaml.XamlServices",
    ];

    /// <summary>Types the image helper must not reference: it must never turn text into a location (a Uri) or use the URI-loading BitmapImage.</summary>
    public static readonly IReadOnlyList<string> BannedHelperTypes =
    [
        "System.Uri",
        "System.Windows.Media.Imaging.BitmapImage",
    ];

    /// <summary>Calls the image helper must not make, as (member, first parameter type): <c>BitmapDecoder.Create(Uri, ...)</c> loads by location.</summary>
    public static readonly IReadOnlyList<(string Member, string FirstParameter)> BannedHelperCalls =
    [
        ("System.Windows.Media.Imaging.BitmapDecoder::Create", "System.Uri"),
    ];

    /// <summary>
    /// <c>ProcessStartInfo</c> members that could run something other than the named program or run it as someone else.
    /// <c>UseShellExecute</c> is included: it defaults to false on .NET and the app has no browser-launch path that needs it.
    /// </summary>
    public static readonly IReadOnlySet<string> BannedProcessStartInfoSetters = new HashSet<string>(StringComparer.Ordinal)
    {
        "set_Verb", "set_UserName", "set_Password", "set_Domain", "set_UseShellExecute",
    };

    /// <summary>Compiler/WPF-generated types allowed to use reflection (they only touch our own XAML types).</summary>
    public static readonly IReadOnlySet<string> GeneratedReflectionUsers = new HashSet<string>(StringComparer.Ordinal)
    {
        "XamlGeneratedNamespace.GeneratedInternalTypeHelper",
    };

    /// <summary>
    /// Narrow exemption: the WPF XAML compiler emits a private <c>_CreateDelegate</c> helper into every window/page/view class
    /// (generated code that binds XAML event handlers to our own methods). It may call <c>Delegate.CreateDelegate</c> and nothing else
    /// from <see cref="BannedMembers"/>, and only in the app assembly. Hand-written code with another method name is still flagged.
    /// </summary>
    public const string XamlGeneratedAssembly = "DBD-Presence";

    public const string XamlGeneratedMethod = "_CreateDelegate";

    /// <summary>Assemblies product code must not reference at all: WMI, performance counters and the event log reader inspect other processes (I1).</summary>
    public static readonly IReadOnlySet<string> BannedProcessInspectionAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Management", "System.Diagnostics.PerformanceCounter", "System.Diagnostics.EventLog",
    };

    public static IReadOnlyList<string> ProcessInspectionAssemblies(AssemblyInspector assembly) =>
        ProcessInspectionAssemblies(assembly, BannedProcessInspectionAssemblies);

    /// <summary>References to any assembly in <paramref name="banned"/>.</summary>
    public static IReadOnlyList<string> ProcessInspectionAssemblies(AssemblyInspector assembly, IReadOnlySet<string> banned) =>
    [
        .. assembly.AssemblyReferences
            .Where(banned.Contains)
            .Select(r => $"references process-inspecting assembly {r}"),
    ];

    /// <summary>Network assemblies product code must not reference (NFR-SEC-01).</summary>
    public static readonly IReadOnlySet<string> NetworkAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Net.Http", "System.Net.Sockets", "System.Net.Requests", "System.Net.Security",
        "System.Net.NameResolution", "System.Net.Ping", "System.Net.WebSockets", "System.Net.WebSockets.Client",
        "System.Net.Mail", "System.Net.Quic", "System.Net.WebClient", "System.Net.HttpListener", "System.Net.NetworkInformation",
    };

    /// <summary>Assembly-name prefixes product assemblies may reference; any new dependency must be added here in review.</summary>
    public static readonly IReadOnlyList<string> AllowedReferencePrefixes =
    [
        "System.", "Microsoft.Win32.", "netstandard", "mscorlib", "WindowsBase", "PresentationCore",
        "PresentationFramework", "System", "DbdPresence.", "DBD-Presence", "CommunityToolkit.Mvvm",
        "H.NotifyIcon", "Microsoft.Extensions.", "Serilog", "Microsoft.Windows.SDK.NET", "WinRT.Runtime",
        // Installer hooks and the signed updater (V2-S2e, V2-S6, ADR-0016); Velopack_UpdatesOnlyThroughTheSignedSource limits it to the App and the signed source.
        "Velopack.",
    ];

    public static IReadOnlyList<string> PInvokeAllowList(AssemblyInspector assembly) =>
    [
        .. assembly.PInvokes
            .Where(p => BannedPInvokes.Contains(p.EntryPoint))
            .Select(p => $"banned native API {p.Module}!{p.EntryPoint} declared in {p.DeclaringType}"),
        .. assembly.PInvokes
            .Where(p => !BannedPInvokes.Contains(p.EntryPoint) && !AllowedPInvokes.Contains(p.EntryPoint))
            .Select(p => $"P/Invoke not on the allow-list: {p.Module}!{p.EntryPoint} in {p.DeclaringType}"),
        .. assembly.PInvokes
            .Where(p => !AllowedModules.Contains(System.IO.Path.GetFileNameWithoutExtension(p.Module)))
            .Select(p => $"P/Invoke targets unapproved module '{p.Module}' ({p.EntryPoint}) in {p.DeclaringType}"),
        .. assembly.PInvokes
            .Where(p => OwnerRestrictedPInvokes.TryGetValue(p.EntryPoint, out var owner) && p.DeclaringType != owner)
            .Select(p => $"{p.EntryPoint} may only be declared in {OwnerRestrictedPInvokes[p.EntryPoint]}, found in {p.DeclaringType}"),
        .. assembly.PInvokes
            .Where(p => p.IsPublic)
            .Select(p => $"P/Invoke {p.DeclaringType}.{p.MethodName} is public; interop must stay private to its assembly"),
    ];

    /// <summary>
    /// Every call to OpenProcess (direct DllImport or LibraryImport wrapper) must be the exact sequence
    /// <c>ldc.i4 0x1000; ldc.i4.0; ldarg.*; call</c>: a literal PROCESS_QUERY_LIMITED_INFORMATION mask, no
    /// inheritance, and a PID argument. Anything computed (fields, locals, arithmetic) is rejected.
    /// </summary>
    public static IReadOnlyList<string> OpenProcessCallSites(AssemblyInspector assembly)
    {
        var violations = new List<string>();
        var targets = assembly.PInvokes.Where(p => p.EntryPoint == "OpenProcess").Select(p => (EntityHandle)p.Handle)
            .Concat(assembly.LibraryImportWrappers.Where(w => w.Value == "OpenProcess").Select(w => (EntityHandle)w.Key))
            .ToHashSet();

        violations.AddRange(assembly.MemberReferences
            .Where(m => m.MemberName == "OpenProcess")
            .Select(m => $"cross-assembly reference to {m.ParentType}::OpenProcess"));

        foreach (var method in assembly.MethodBodies().Where(m => !m.IsLibraryImportWrapper))
        {
            var il = method.Instructions;
            for (var i = 0; i < il.Length; i++)
            {
                if (il[i].OpCode is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Ldftn or ILOpCode.Jmp)
                    || il[i].Operand is not EntityHandle callee || !targets.Contains(callee))
                {
                    continue;
                }

                var compliant = il[i].OpCode == ILOpCode.Call && i >= 3
                    && IsLdcI4(il[i - 3], ProcessQueryLimitedInformation)
                    && IsLdcI4(il[i - 2], 0)
                    && IsLdarg(il[i - 1].OpCode);
                if (!compliant)
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls OpenProcess without the literal PROCESS_QUERY_LIMITED_INFORMATION pattern");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// The named exception to the <c>Process.Start</c> allow-list (ADR-0016): the one type that launches the image
    /// helper (ADR-0013, FR-PACK-10). Every other caller of <c>Process.Start</c> is a violation. Further launchers
    /// (browser, update helper) must be added here by name in review.
    /// </summary>
    public const string ImageHelperLauncher = "DbdPresence.Platform.Windows.Images.SafeImageLoader";

    /// <summary>
    /// The second named <c>Process.Start</c> exception (ADR-0016): the browser launcher, which opens Discord's OAuth page through the shell.
    /// It is the only type that may set <c>ProcessStartInfo.UseShellExecute</c>, and only in a method that contains
    /// <see cref="DiscordAuthorizeLiteral"/> (the check that refuses every other URL).
    /// </summary>
    public const string BrowserLauncher = "DbdPresence.Platform.Windows.Accounts.DiscordBrowserLauncher";

    /// <summary>The URL prefix literal the browser launcher must compare against before starting anything.</summary>
    public const string DiscordAuthorizeLiteral = "https://discord.com/oauth2/authorize?";

    /// <summary>The assembly that must contain <see cref="ImageHelperLauncher"/>; a same-named type elsewhere is not the launcher.</summary>
    public const string ImageHelperLauncherAssembly = "DbdPresence.Platform.Windows";

    /// <summary>The only program the image-helper launcher may start; the launching method must contain this literal.</summary>
    public const string ImageHelperFileName = "DbdPresence.ImageHelper.exe";

    /// <summary>
    /// Members of a process this app started itself that <see cref="ImageHelperLauncher"/> needs to wait for and stop
    /// its helper. They are denied everywhere else (they could be used on a foreign process).
    /// </summary>
    public static readonly IReadOnlySet<string> LauncherOnlyProcessMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "WaitForExit", "Kill", "get_ExitCode", "get_HasExited",
    };

    /// <summary>Whether <paramref name="declaringType"/> is the browser launcher (or nested in it) in the right assembly.</summary>
    public static bool IsBrowserLauncher(AssemblyInspector assembly, string declaringType) =>
        assembly.AssemblyName == ImageHelperLauncherAssembly
        && (declaringType == BrowserLauncher || declaringType.StartsWith(BrowserLauncher + "+", StringComparison.Ordinal));

    public static IReadOnlyList<string> ProcessMembers(AssemblyInspector assembly)
    {
        var violations = assembly.MemberReferences
            .Where(m => m.ParentType == "System.Diagnostics.Process"
                && !AllowedProcessMembers.Contains(m.MemberName) && !LauncherOnlyProcessMembers.Contains(m.MemberName))
            .Select(m => m.MemberName)
            .Distinct(StringComparer.Ordinal)
            .Select(name => $"Process.{name} is outside the allow-list (it opens a process handle)")
            .ToList();

        foreach (var method in assembly.MethodBodies())
        {
            var isLauncher = assembly.AssemblyName == ImageHelperLauncherAssembly
                && (method.DeclaringType == ImageHelperLauncher
                    || method.DeclaringType.StartsWith(ImageHelperLauncher + "+", StringComparison.Ordinal));
            var isBrowser = IsBrowserLauncher(assembly, method.DeclaringType);
            var literals = method.Instructions.Where(i => i.OpCode == ILOpCode.Ldstr).Select(i => (string)i.Operand!).ToArray();
            var starts = 0;
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Ldftn) || instruction.Operand is not EntityHandle handle)
                {
                    continue;
                }

                var target = assembly.Describe(handle);
                const string prefix = "System.Diagnostics.Process::";
                if (!target.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var member = target[prefix.Length..];
                if (member == "Start")
                {
                    // I1: the literal check alone is per method, so also pin the shape of the call. Only the overload that takes a
                    // ProcessStartInfo (whose setters are checked by ProcessStartInfoSetters) is allowed, and one call per method, so a
                    // second, computed Start cannot ride on the literal that licenses the first.
                    starts++;
                    if (assembly.ParameterTypes(handle) is not ["System.Diagnostics.ProcessStartInfo"])
                    {
                        violations.Add($"{method.DeclaringType}.{method.Name} calls a Process.Start overload other than Start(ProcessStartInfo)");
                    }
                    else if (starts > 1)
                    {
                        violations.Add($"{method.DeclaringType}.{method.Name} calls Process.Start more than once; a launcher method starts exactly one process");
                    }
                }

                if (member == "Start" && !(isLauncher && literals.Contains(ImageHelperFileName)) && !(isBrowser && literals.Contains(DiscordAuthorizeLiteral)))
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls Process.Start; only {ImageHelperLauncher} (for {ImageHelperFileName}) and {BrowserLauncher} (for {DiscordAuthorizeLiteral}) in {ImageHelperLauncherAssembly} may");
                }
                else if (LauncherOnlyProcessMembers.Contains(member) && !isLauncher)
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} uses Process.{member}, which is reserved for {ImageHelperLauncher}");
                }
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> DynamicCode(AssemblyInspector assembly)
    {
        var violations = new List<string>();
        foreach (var method in assembly.MethodBodies())
        {
            if (GeneratedReflectionUsers.Contains(method.DeclaringType))
            {
                continue;
            }

            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode == ILOpCode.Calli)
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} uses calli (function-pointer call)");
                }
                else if (instruction.Operand is EntityHandle handle && instruction.OpCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Ldftn
                    && IsBannedMember(assembly.Describe(handle))
                    && !(assembly.AssemblyName == XamlGeneratedAssembly && method.Name == XamlGeneratedMethod
                        && assembly.Describe(handle) == "System.Delegate::CreateDelegate"))
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls {assembly.Describe(handle)}");
                }
            }
        }

        return violations;
    }

    /// <summary>Product code must not generate code at run time or open locations through the Windows shell launcher.</summary>
    public static IReadOnlyList<string> BannedTypes(AssemblyInspector assembly) =>
    [
        .. BannedTypes(assembly, assembly.AssemblyName == ImageHelperLauncherAssembly ? [.. BannedProductTypes.Where(t => t != LauncherType)] : BannedProductTypes),
        .. LauncherUse(assembly),
    ];

    /// <summary>The WinRT launcher type. It stays banned everywhere except the one use in <see cref="InstalledAppsLauncher"/>.</summary>
    public const string LauncherType = "Windows.System.Launcher";

    /// <summary>
    /// The one named user of <see cref="LauncherType"/> (Microsoft Store package work, FR-LIFE-01 and the Uninstall button): it opens Windows' Installed
    /// apps page. It must be in <see cref="ImageHelperLauncherAssembly"/>, may call only <c>LaunchUriAsync</c>, and must contain the literal
    /// <see cref="InstalledAppsLiteral"/>. <c>LauncherOptions</c> and every other launcher member stay banned.
    /// </summary>
    public const string InstalledAppsLauncher = "DbdPresence.Platform.Windows.Startup.InstalledAppsLauncher";

    /// <summary>The only address <see cref="InstalledAppsLauncher"/> may open.</summary>
    public const string InstalledAppsLiteral = "ms-settings:appsfeatures";

    /// <summary>The one launcher member <see cref="InstalledAppsLauncher"/> may call.</summary>
    public const string AllowedLauncherMember = "Windows.System.Launcher::LaunchUriAsync";

    /// <summary>Whether <paramref name="declaringType"/> is the installed-apps launcher (or nested in it) in the right assembly.</summary>
    public static bool IsInstalledAppsLauncher(AssemblyInspector assembly, string declaringType) =>
        assembly.AssemblyName == ImageHelperLauncherAssembly
        && (declaringType == InstalledAppsLauncher || declaringType.StartsWith(InstalledAppsLauncher + "+", StringComparison.Ordinal));

    /// <summary>Calls to <c>Windows.System.Launcher</c> members in the product assembly that may hold the exemption; any other call there is a violation.</summary>
    public static IReadOnlyList<string> LauncherUse(AssemblyInspector assembly)
    {
        if (assembly.AssemblyName != ImageHelperLauncherAssembly)
        {
            return [];
        }

        var bodies = assembly.MethodBodies().ToList();
        var literalPresent = bodies.Any(m => IsInstalledAppsLauncher(assembly, m.DeclaringType)
            && m.Instructions.Any(i => i.OpCode == ILOpCode.Ldstr && (string?)i.Operand == InstalledAppsLiteral));
        var violations = new List<string>();
        foreach (var method in bodies)
        {
            foreach (var instruction in method.Instructions)
            {
                if (instruction.Operand is EntityHandle handle && instruction.OpCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldtoken
                    && assembly.Describe(handle) is var target && target.StartsWith(LauncherType + "::", StringComparison.Ordinal)
                    && !(IsInstalledAppsLauncher(assembly, method.DeclaringType) && literalPresent && target == AllowedLauncherMember))
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls {target}; only {InstalledAppsLauncher} may call {AllowedLauncherMember}, with the literal {InstalledAppsLiteral}");
                }
            }
        }

        return violations;
    }

    /// <summary>Type references matching <paramref name="banned"/>; entries ending in "." match a namespace, others the exact type and its nested types.</summary>
    public static IReadOnlyList<string> BannedTypes(AssemblyInspector assembly, IReadOnlyList<string> banned) =>
    [
        .. assembly.TypeReferences
            .Where(t => banned.Any(b => b.EndsWith('.') ? t.StartsWith(b, StringComparison.Ordinal) : t == b || t.StartsWith(b + "+", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Select(t => $"references banned type {t}"),
    ];

    /// <summary>The image helper (untrusted input): no Uri, no BitmapImage, no Uri-based decoder creation.</summary>
    public static IReadOnlyList<string> ImageHelper(AssemblyInspector assembly) =>
    [
        .. BannedTypes(assembly, BannedHelperTypes),
        .. BannedCallsWithFirstParameter(assembly, BannedHelperCalls),
    ];

    /// <summary>Calls to the given members whose first parameter has the given type.</summary>
    public static IReadOnlyList<string> BannedCallsWithFirstParameter(AssemblyInspector assembly, IReadOnlyList<(string Member, string FirstParameter)> banned)
    {
        var violations = new List<string>();
        foreach (var method in assembly.MethodBodies())
        {
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn) || instruction.Operand is not EntityHandle handle)
                {
                    continue;
                }

                var target = assembly.Describe(handle);
                if (banned.Any(b => b.Member == target) && assembly.ParameterTypes(handle) is [var first, ..]
                    && banned.Any(b => b.Member == target && b.FirstParameter == first))
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls {target}({first}, ...)");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Nothing may set the <c>ProcessStartInfo</c> members in <see cref="BannedProcessStartInfoSetters"/>: they would allow shell verbs
    /// ("runas"), running as another user, or shell-executing a document or URL instead of the named image helper.
    /// </summary>
    public static IReadOnlyList<string> ProcessStartInfoSetters(AssemblyInspector assembly)
    {
        const string prefix = "System.Diagnostics.ProcessStartInfo::";
        var violations = new List<string>();
        foreach (var method in assembly.MethodBodies())
        {
            var browserMethod = IsBrowserLauncher(assembly, method.DeclaringType)
                && method.Instructions.Any(i => i.OpCode == ILOpCode.Ldstr && (string?)i.Operand == DiscordAuthorizeLiteral);
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode is ILOpCode.Call or ILOpCode.Callvirt && instruction.Operand is EntityHandle handle
                    && assembly.Describe(handle) is var target && target.StartsWith(prefix, StringComparison.Ordinal)
                    && BannedProcessStartInfoSetters.Contains(target[prefix.Length..])
                    && !(browserMethod && target[prefix.Length..] == "set_UseShellExecute"))
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} calls ProcessStartInfo.{target[prefix.Length..]}");
                }
            }
        }

        return violations;
    }

    /// <summary>The one product assembly allowed networking (ADR-0016).</summary>
    public const string OnlineAssembly = "DbdPresence.Online";

    /// <summary>Network assemblies <see cref="OnlineAssembly"/> may reference: HTTP, the loopback listener's sockets and TLS.</summary>
    public static readonly IReadOnlySet<string> OnlineNetworkAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Net.Http", "System.Net.Sockets", "System.Net.Security",
    };

    /// <summary>
    /// Network types <see cref="OnlineAssembly"/> may use (entries ending in "." are namespace prefixes). Web sockets, mail, ping,
    /// WebRequest, HttpListener and network-information types stay banned there too.
    /// </summary>
    public static readonly IReadOnlyList<string> OnlineNetworkTypes =
    [
        "System.Net.Http.", "System.Net.Sockets.", "System.Net.Security.",
        "System.Net.IPAddress", "System.Net.IPEndPoint", "System.Net.EndPoint", "System.Net.DnsEndPoint", "System.Net.HttpStatusCode", "System.Net.IWebProxy",
    ];

    public static IReadOnlyList<string> Network(AssemblyInspector assembly) =>
        assembly.AssemblyName == OnlineAssembly
            ? Network(assembly, OnlineNetworkAssemblies, OnlineNetworkTypes)
            : Network(assembly, new HashSet<string>(), []);

    /// <summary>Network references other than the given allowed assemblies and types.</summary>
    public static IReadOnlyList<string> Network(AssemblyInspector assembly, IReadOnlySet<string> allowedAssemblies, IReadOnlyList<string> allowedTypes) =>
    [
        .. assembly.AssemblyReferences.Where(r => NetworkAssemblies.Contains(r) && !allowedAssemblies.Contains(r)).Select(r => $"references network assembly {r}"),
        .. assembly.TypeReferences
            .Where(t => (t.StartsWith("System.Net.", StringComparison.Ordinal) || t.StartsWith("Windows.Networking.", StringComparison.Ordinal)
                || t.StartsWith("Windows.Web.", StringComparison.Ordinal))
                && !allowedTypes.Any(a => a.EndsWith('.') ? t.StartsWith(a, StringComparison.Ordinal) : t == a || t.StartsWith(a + "+", StringComparison.Ordinal)))
            .Select(t => $"references network type {t}"),
    ];

    /// <summary>PipeOptions.CurrentUserOnly: only a server owned by the current user is accepted (S-M1).</summary>
    public const int PipeOptionsCurrentUserOnly = 0x20000000;

    /// <summary>TokenImpersonationLevel.Identification: the server may identify but never act as the client (S-M1).</summary>
    public const int ImpersonationIdentification = 2;

    /// <summary>
    /// Named pipes are allowed only to the local machine (server name ".") and only through the 5-argument
    /// constructor whose last two arguments are literals: PipeOptions including CurrentUserOnly, then
    /// TokenImpersonationLevel.Identification. A server that another account created therefore cannot be
    /// connected to, and a server can never impersonate us.
    /// </summary>
    public static IReadOnlyList<string> LocalPipesOnly(AssemblyInspector assembly)
    {
        var violations = new List<string>();
        foreach (var method in assembly.MethodBodies())
        {
            var il = method.Instructions;
            var constructions = Enumerable.Range(0, il.Length)
                .Where(i => il[i].OpCode == ILOpCode.Newobj && il[i].Operand is EntityHandle h
                    && assembly.Describe(h) == "System.IO.Pipes.NamedPipeClientStream::.ctor")
                .ToArray();
            if (constructions.Length == 0)
            {
                continue;
            }

            var strings = il.Where(i => i.OpCode == ILOpCode.Ldstr).Select(i => (string)i.Operand!).ToArray();
            if (!strings.Contains(".") || strings.Any(s => s.StartsWith(@"\\", StringComparison.Ordinal)))
            {
                violations.Add($"{method.DeclaringType}.{method.Name} creates a NamedPipeClientStream without the literal local server \".\"");
            }

            foreach (var at in constructions)
            {
                var hardened = at >= 2
                    && TryLdcI4(il[at - 1], out var level) && level == ImpersonationIdentification
                    && TryLdcI4(il[at - 2], out var options) && (options & PipeOptionsCurrentUserOnly) != 0;
                if (!hardened)
                {
                    violations.Add($"{method.DeclaringType}.{method.Name} creates a NamedPipeClientStream without the literal PipeOptions.CurrentUserOnly and TokenImpersonationLevel.Identification arguments (5-argument constructor)");
                }
            }
        }

        return violations;
    }

    public static IReadOnlyList<string> AssemblyReferenceAllowList(AssemblyInspector assembly) =>
    [
        .. assembly.AssemblyReferences
            .Where(r => !AllowedReferencePrefixes.Any(p => r == p.TrimEnd('.') || r.StartsWith(p, StringComparison.Ordinal)))
            .Select(r => $"references unapproved assembly {r} (add it to the allow-list only after licence and security review)"),
    ];

    /// <summary>All rules applied to product code.</summary>
    public static IReadOnlyList<string> All(AssemblyInspector assembly) =>
    [
        .. PInvokeAllowList(assembly), .. OpenProcessCallSites(assembly), .. ProcessMembers(assembly),
        .. DynamicCode(assembly), .. BannedTypes(assembly), .. ProcessInspectionAssemblies(assembly), .. ProcessStartInfoSetters(assembly), .. Network(assembly), .. LocalPipesOnly(assembly), .. AssemblyReferenceAllowList(assembly), .. ComImports(assembly),
    ];

    private static bool IsBannedMember(string described) =>
        BannedMembers.Contains(described) || BannedMemberPrefixes.Any(p => described.StartsWith(p, StringComparison.Ordinal));

    private static bool IsLdcI4(Instruction instruction, int value) => TryLdcI4(instruction, out var v) && v == value;

    private static bool TryLdcI4(Instruction instruction, out int value)
    {
        if (instruction.OpCode is ILOpCode.Ldc_i4 or ILOpCode.Ldc_i4_s or (>= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8)
            && instruction.Operand is int v)
        {
            value = v;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// The only host names that may appear as string literals in <see cref="OnlineAssembly"/>: the two API hosts and the two CDN hosts
    /// (ADR-0016; the CDN host is restricted to GET of pack file paths by the handler), Discord's OAuth host (only to build the authorize URL
    /// for the browser launcher) and the loopback address of the sign-in listener.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedOnlineHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.arcavale.com", "api-staging.arcavale.com", "cdn.arcavale.com", "cdn-staging.arcavale.com", "discord.com", "127.0.0.1",
    };

    /// <summary>Host names found in string literals (URLs, and bare names ending in a common top-level domain) that are not in <paramref name="allowed"/>.</summary>
    public static IReadOnlyList<string> HostLiterals(AssemblyInspector assembly, IReadOnlySet<string> allowed)
    {
        var violations = new List<string>();
        foreach (var method in assembly.MethodBodies())
        {
            foreach (var literal in method.Instructions.Where(i => i.OpCode == ILOpCode.Ldstr).Select(i => (string)i.Operand!))
            {
                foreach (System.Text.RegularExpressions.Match match in s_hostPattern.Matches(literal))
                {
                    var host = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["bare"].Value;
                    if (!allowed.Contains(host))
                    {
                        violations.Add($"{method.DeclaringType}.{method.Name} has the host literal '{host}'");
                    }
                }
            }
        }

        return violations;
    }

    private static readonly System.Text.RegularExpressions.Regex s_hostPattern = new(
        @"https?://(?<url>[A-Za-z0-9.-]+)|(?<![A-Za-z0-9.-])(?<bare>(?:[A-Za-z0-9-]+\.)+(?:com|net|org|io|gg|dev|app|co|uk|tv|me|xyz|info))(?![A-Za-z0-9-])",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsLdarg(ILOpCode op) =>
        op is ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3 or ILOpCode.Ldarg_s or ILOpCode.Ldarg;

    /// <summary>
    /// Every construction of Velopack's <c>UpdateManager</c> (V2-S6, ADR-0016): a <c>newobj</c>, and also a <c>call</c> to its constructor,
    /// which is how a subclass constructor builds its base (security review 2026-10-25, L8). Each must be in <c>DbdPresence.App.Updates</c>
    /// and use the overload that takes an <c>IUpdateSource</c>; the other overloads build Velopack's own web or Flow source.
    /// </summary>
    /// <param name="assembly">The assembly to check.</param>
    /// <param name="constructions">How many constructions were found (so a caller can refuse a vacuous pass).</param>
    /// <returns>The offending construction sites.</returns>
    public static IReadOnlyList<string> UpdateManagerConstructions(AssemblyInspector assembly, out int constructions)
    {
        const string SourceInterface = "Velopack.Sources.IUpdateSource";
        var offenders = new List<string>();
        constructions = 0;
        foreach (var body in assembly.MethodBodies())
        {
            foreach (var instruction in body.Instructions)
            {
                if (instruction.OpCode is not (ILOpCode.Newobj or ILOpCode.Call) || instruction.Operand is not EntityHandle handle
                    || assembly.Describe(handle) != "Velopack.UpdateManager::.ctor")
                {
                    continue;
                }

                constructions++;
                var parameters = assembly.ParameterTypes(handle);
                if (!body.DeclaringType.StartsWith("DbdPresence.App.Updates.", StringComparison.Ordinal) || parameters.Count == 0 || parameters[0] != SourceInterface)
                {
                    offenders.Add($"{body.DeclaringType}::{body.Name} constructs UpdateManager({string.Join(", ", parameters)}); only DbdPresence.App.Updates may, with an IUpdateSource");
                }
            }
        }

        return offenders;
    }
}
