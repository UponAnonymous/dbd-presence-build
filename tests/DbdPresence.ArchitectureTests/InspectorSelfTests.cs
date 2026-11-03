using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace DbdPresence.ArchitectureTests;

/// <summary>
/// Proves each <see cref="SafetyRules"/> rule detects the deliberate violations in <see cref="Samples"/>
/// (compiled into this test assembly) and accepts the compliant patterns, so passing product checks are
/// never vacuous (QA review AR-5). Also checks the IL decoder against all of System.Private.CoreLib.
/// </summary>
public sealed partial class InspectorSelfTests
{
    private static AssemblyInspector Self() => new(typeof(InspectorSelfTests).Assembly.Location);

    [Fact]
    public void PInvokeRule_FlagsBannedUnlistedForeignModuleAndPublicDeclarations()
    {
        using var self = Self();

        var violations = SafetyRules.PInvokeAllowList(self);

        Assert.Contains(violations, v => v.Contains("banned native API kernel32.dll!ReadProcessMemory", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("not on the allow-list: user32.dll!GetKeyState", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("unapproved module 'evil.dll'", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("PublicInterop", StringComparison.Ordinal) && v.Contains("is public", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("DwmSetWindowAttribute may only be declared in", StringComparison.Ordinal) && v.Contains("Samples+StrayWindowStyling", StringComparison.Ordinal));
    }

    [Fact]
    public void ComImportRule_FlagsUnlistedComImportTypes()
    {
        using var self = Self();

        var violations = SafetyRules.ComImports(self);

        Assert.Contains(violations, v => v.Contains("Samples+StrayComInterface", StringComparison.Ordinal));
    }

    [Fact]
    public void ComImportRule_AllowsExactlyTheNamedCaptureInterop()
    {
        Assert.Equal(
            ["DbdPresence.Platform.Windows.Capture.CaptureInterop+IGraphicsCaptureItemInterop"],
            SafetyRules.AllowedComImports.Keys.ToArray());
    }

    [Fact]
    public void OpenProcessRule_AcceptsLiteralLimitedMaskOnly()
    {
        using var self = Self();

        var violations = SafetyRules.OpenProcessCallSites(self);

        Assert.DoesNotContain(violations, v => v.Contains("Samples+Compliant", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+WrongMask.Open", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+MaskFromField.Open", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+MaskFromParameter.Open", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+NegativeMask.Open", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+NameLooksGenerated.Evil__PInvoke", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+DirectDllImport.Open", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessRule_FlagsHandleOpeningMembers()
    {
        using var self = Self();

        Assert.Contains(SafetyRules.ProcessMembers(self), v => v.Contains("Process.get_StartTime", StringComparison.Ordinal));
        Assert.Contains(SafetyRules.ProcessMembers(self), v => v.Contains("Process.get_MainModule", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessRule_AllowsStartAndStopOnlyForTheNamedImageHelperLauncher()
    {
        using var self = Self();

        var violations = SafetyRules.ProcessMembers(self);

        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.Launch calls Process.Start", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.Stop uses Process.Kill", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.Wait uses Process.WaitForExit", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.Wait uses Process.get_ExitCode", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicCodeRule_FlagsNativeLibraryDelegatesCalliAndReflection()
    {
        using var self = Self();

        var violations = SafetyRules.DynamicCode(self);

        Assert.Contains(violations, v => v.Contains("NativeLibrary::GetExport", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Marshal::GetDelegateForFunctionPointer", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("uses calli", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Assembly::LoadFrom", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Activator::CreateInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessRule_RejectsALookAlikeLauncherInTheWrongAssembly()
    {
        using var self = Self();

        // The sample type has the launcher's exact full name and the helper's file name, but lives in the test assembly.
        var violations = SafetyRules.ProcessMembers(self);

        Assert.Contains(violations, v => v.Contains("DbdPresence.Platform.Windows.Images.SafeImageLoader.Launch calls Process.Start", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("DbdPresence.Platform.Windows.Images.SafeImageLoader.Stop uses Process.Kill", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessRule_RejectsALookAlikeBrowserLauncherInTheWrongAssembly()
    {
        using var self = Self();

        // Same full name and URL literal as the real browser launcher, but in the test assembly: neither Start nor UseShellExecute is allowed.
        Assert.Contains(SafetyRules.ProcessMembers(self), v => v.Contains("DbdPresence.Platform.Windows.Accounts.DiscordBrowserLauncher.Open calls Process.Start", StringComparison.Ordinal));
        Assert.Contains(
            SafetyRules.ProcessStartInfoSetters(self),
            v => v.Contains("DbdPresence.Platform.Windows.Accounts.DiscordBrowserLauncher.Open", StringComparison.Ordinal) && v.EndsWith("set_UseShellExecute", StringComparison.Ordinal));
        Assert.False(SafetyRules.IsBrowserLauncher(self, SafetyRules.BrowserLauncher));
    }

    [Fact]
    public void CertificateRule_FlagsCustomValidationCallbacksEverywhere()
    {
        using var self = Self();

        var members = SafetyRules.DynamicCode(self);

        Assert.Contains(members, v => v.Contains("Samples+CertCallbacks.Lax calls System.Net.Http.HttpClientHandler::set_ServerCertificateCustomValidationCallback", StringComparison.Ordinal));
        Assert.Contains(members, v => v.Contains("Samples+CertCallbacks.Lax calls System.Net.Http.HttpClientHandler::get_DangerousAcceptAnyServerCertificateValidator", StringComparison.Ordinal));
        Assert.Contains(members, v => v.Contains("Samples+CertCallbacks.Options calls System.Net.Security.SslClientAuthenticationOptions::set_RemoteCertificateValidationCallback", StringComparison.Ordinal));
        Assert.Contains(SafetyRules.BannedTypes(self), v => v.Contains("System.Net.Security.RemoteCertificateValidationCallback", StringComparison.Ordinal));
        Assert.Contains("System.Net.ServicePointManager::set_ServerCertificateValidationCallback", SafetyRules.BannedMembers);
    }

    [Fact]
    public void NetworkRule_ForOnlineAllowsHttpSocketsAndTlsButNothingElse()
    {
        using var self = Self();

        var violations = SafetyRules.Network(self, SafetyRules.OnlineNetworkAssemblies, SafetyRules.OnlineNetworkTypes);

        Assert.DoesNotContain(violations, v => v.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("System.Net.Security.SslStream", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v == "references network assembly System.Net.Http");
        Assert.Contains(violations, v => v.Contains("System.Net.NetworkInformation.Ping", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Net.WebSockets.ClientWebSocket", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Net.HttpListener", StringComparison.Ordinal));
        Assert.Equal("DbdPresence.Online", SafetyRules.OnlineAssembly);
    }

    [Fact]
    public void ProcessStartInfoRule_FlagsVerbUserPasswordDomainAndShellExecute()
    {
        using var self = Self();

        var violations = SafetyRules.ProcessStartInfoSetters(self);

        foreach (var setter in new[] { "set_Verb", "set_UserName", "set_Password", "set_Domain", "set_UseShellExecute" })
        {
            Assert.Contains(violations, v => v.Contains("Samples+StartInfo.Elevated", StringComparison.Ordinal) && v.EndsWith(setter, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(violations, v => v.Contains("Samples+StartInfo.Plain", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicCodeRule_FlagsTheReflectionGaps()
    {
        using var self = Self();

        var violations = SafetyRules.DynamicCode(self);

        Assert.Contains(violations, v => v.Contains("Samples+Gaps.Invoke calls System.Delegate::DynamicInvoke", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Gaps.Bind calls System.Reflection.MethodInfo::CreateDelegate", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Gaps.StaticBind calls System.Delegate::CreateDelegate", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Gaps.CompileLambda calls System.Linq.Expressions.LambdaExpression::Compile", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Gaps.CompileTyped calls System.Linq.Expressions.Expression`1::Compile", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Gaps.LoadContext calls System.Runtime.Loader.AssemblyLoadContext::LoadFromAssemblyPath", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicCodeRule_FlagsLateBoundAndByNameReflection()
    {
        // Security review 2026-10-25, I1.
        using var self = Self();

        var violations = SafetyRules.DynamicCode(self);

        foreach (var (method, call) in new[]
        {
            ("Invoke", "System.Reflection.ConstructorInfo::Invoke"),
            ("InvokeMethod", "System.Reflection.MethodBase::Invoke"),
            ("ReadField", "System.Reflection.FieldInfo::GetValue"),
            ("WriteField", "System.Reflection.FieldInfo::SetValue"),
            ("ReadProperty", "System.Reflection.PropertyInfo::GetValue"),
            ("WriteProperty", "System.Reflection.PropertyInfo::SetValue"),
            ("AssemblyType", "System.Reflection.Assembly::GetType"),
            ("AssemblyCreate", "System.Reflection.Assembly::CreateInstance"),
            ("TypeByName", "System.Type::GetType"),
            ("ByProgId", "System.Type::GetTypeFromProgID"),
            ("ByClsid", "System.Type::GetTypeFromCLSID"),
            ("ActivateByName", "System.Activator::CreateInstance"),
            ("ActivateFrom", "System.Activator::CreateInstanceFrom"),
            ("DomainLoad", "System.AppDomain::Load"),
            ("DomainCreate", "System.AppDomain::CreateInstance"),
            ("DomainExecute", "System.AppDomain::ExecuteAssembly"),
            ("RuntimeLookup", "System.Reflection.RuntimeReflectionExtensions::GetRuntimeField"),
            ("Moniker", "System.Runtime.InteropServices.Marshal::BindToMoniker"),
        })
        {
            Assert.True(
                violations.Any(v => v.Contains($"Samples+LateBound.{method} calls {call}", StringComparison.Ordinal)),
                $"LateBound.{method} was not flagged as calling {call}");
        }
    }

    [Fact]
    public void BannedTypeRule_FlagsProcessInspectingTypes()
    {
        using var self = Self();

        // ProcessModule and ProcessThread come from the fixture; WMI, Windows.System.Diagnostics and PerformanceCounter cannot be
        // compiled into this assembly (not referenced), so those entries are pinned and the matching engine is proven above.
        var violations = SafetyRules.BannedTypes(self);
        Assert.Contains(violations, v => v == "references banned type System.Diagnostics.ProcessModule");
        Assert.Contains(violations, v => v == "references banned type System.Diagnostics.ProcessThreadCollection");
        foreach (var banned in new[]
        {
            "System.Management.", "Windows.System.Diagnostics.", "Windows.System.AppDiagnosticInfo", "System.Diagnostics.PerformanceCounter",
            "System.Diagnostics.Eventing.Reader.",
        })
        {
            Assert.Contains(banned, SafetyRules.BannedProductTypes);
        }

        Assert.Contains("System.Management", SafetyRules.BannedProcessInspectionAssemblies);
        Assert.Contains(SafetyRules.ProcessInspectionAssemblies(self, new HashSet<string> { "System.Linq" }), v => v == "references process-inspecting assembly System.Linq");
        Assert.Empty(SafetyRules.ProcessInspectionAssemblies(self));
    }

    [Fact]
    public void ProcessRule_PinsTheShapeOfEveryStartCall()
    {
        using var self = Self();

        var violations = SafetyRules.ProcessMembers(self);

        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.Launch calls a Process.Start overload other than Start(ProcessStartInfo)", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+ProcessUse.TwoStarts calls Process.Start more than once", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Samples+ProcessUse.OneStart calls a Process.Start overload", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Samples+ProcessUse.OneStart calls Process.Start more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void BannedTypeRule_FlagsReflectionEmitAndMatchesByNamespaceOrExactName()
    {
        using var self = Self();

        Assert.Contains(SafetyRules.BannedTypes(self), v => v.Contains("System.Reflection.Emit.DynamicMethod", StringComparison.Ordinal));
        Assert.Contains(SafetyRules.BannedTypes(self, ["System.Uri"]), v => v == "references banned type System.Uri");
        Assert.Empty(SafetyRules.BannedTypes(self, ["System.Uri2"]));
        Assert.Empty(SafetyRules.BannedTypes(self, ["System.Ur"]));
    }

    [Fact]
    public void LauncherRule_AllowsOnlyTheNamedInstalledAppsLauncherInTheProductAssembly()
    {
        using var self = Self();

        // The test assembly is not the product assembly, so even a type with the launcher's exact name is not exempt here, and the rule is silent.
        Assert.False(SafetyRules.IsInstalledAppsLauncher(self, SafetyRules.InstalledAppsLauncher));
        Assert.Empty(SafetyRules.LauncherUse(self));
        Assert.Equal("ms-settings:appsfeatures", SafetyRules.InstalledAppsLiteral);
        Assert.Equal("Windows.System.Launcher::LaunchUriAsync", SafetyRules.AllowedLauncherMember);
        Assert.Contains("Windows.System.Launcher", SafetyRules.BannedProductTypes);
        Assert.Contains("Windows.System.LauncherOptions", SafetyRules.BannedProductTypes);
    }

    [Fact]
    public void BannedTypeLists_NameTheLauncherAndTheHelperTypes()
    {
        // These types cannot be compiled into a net10.0 test assembly, so the lists are pinned here and the engine is
        // exercised above with types that can.
        Assert.Contains("Windows.System.Launcher", SafetyRules.BannedProductTypes);
        Assert.Contains("System.Reflection.Emit.", SafetyRules.BannedProductTypes);
        Assert.Contains("System.Windows.Markup.XamlReader", SafetyRules.BannedProductTypes);
        Assert.Contains("System.Xaml.XamlServices", SafetyRules.BannedProductTypes);
        Assert.Contains("System.Uri", SafetyRules.BannedHelperTypes);
        Assert.Contains("System.Windows.Media.Imaging.BitmapImage", SafetyRules.BannedHelperTypes);
        Assert.Contains(("System.Windows.Media.Imaging.BitmapDecoder::Create", "System.Uri"), SafetyRules.BannedHelperCalls);
    }

    [Fact]
    public void HelperRule_FlagsUriUse()
    {
        using var self = Self();

        Assert.Contains(SafetyRules.ImageHelper(self), v => v.Contains("System.Uri", StringComparison.Ordinal));
    }

    [Fact]
    public void ParameterRule_DistinguishesTheFirstParameterType()
    {
        using var self = Self();

        var violations = SafetyRules.BannedCallsWithFirstParameter(self, [("System.Uri::TryCreate", "System.Uri")]);

        Assert.Contains(violations, v => v.Contains("Samples+UriCalls.FromBase", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Samples+UriCalls.FromText", StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkRule_FlagsNetworkTypesAndAssemblies()
    {
        using var self = Self();

        var violations = SafetyRules.Network(self);

        Assert.Contains(violations, v => v.Contains("System.Net.Http.HttpClient", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Net.NetworkInformation.Ping", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("System.Net.Security.SslStream", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("network assembly System.Net.Http", StringComparison.Ordinal));
    }

    [Fact]
    public void PipeRule_FlagsRemoteServerAndAcceptsLocal()
    {
        using var self = Self();

        var violations = SafetyRules.LocalPipesOnly(self);

        Assert.Contains(violations, v => v.Contains("Samples+Pipes.Remote ", StringComparison.Ordinal) && v.Contains("local server", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Samples+Pipes.Local ", StringComparison.Ordinal));
    }

    [Fact]
    public void PipeRule_FlagsTheOldFourArgumentFormAndMissingHardening()
    {
        using var self = Self();

        var violations = SafetyRules.LocalPipesOnly(self);

        Assert.Contains(violations, v => v.Contains("Samples+Pipes.LegacyFourArgument ", StringComparison.Ordinal) && v.Contains("CurrentUserOnly", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Pipes.WithoutCurrentUserOnly ", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("Samples+Pipes.ImpersonationAllowed ", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceRule_FlagsUnapprovedAssemblies()
    {
        using var self = Self();

        Assert.Contains(SafetyRules.AssemblyReferenceAllowList(self), v => v.Contains("xunit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IlDecoder_DecodesEveryMethodOfCoreLib()
    {
        using var coreLib = new AssemblyInspector(typeof(object).Assembly.Location);
        var methods = 0;
        var calls = 0;

        foreach (var method in coreLib.MethodBodies())
        {
            methods++;
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj)
                {
                    calls++;
                    var handle = Assert.IsType<EntityHandle>(instruction.Operand);
                    Assert.True(
                        handle.Kind is HandleKind.MethodDefinition or HandleKind.MemberReference or HandleKind.MethodSpecification,
                        $"{method.DeclaringType}.{method.Name}: call operand decoded as {handle.Kind}");
                }
            }
        }

        Assert.True(methods > 10_000 && calls > 100_000, $"decoded {methods} methods / {calls} calls");
    }

    [Fact]
    public void UpdateManagerRule_FindsTheBaseCallOfASubclass_NotOnlyNewobj()
    {
        // Security review 2026-10-25, L8: a subclass builds its UpdateManager base with "call .ctor", which the newobj-only rule missed.
        using var self = Self();

        var violations = SafetyRules.UpdateManagerConstructions(self, out var constructions);

        Assert.True(constructions >= 1);
        Assert.Contains(violations, v => v.Contains("Samples+SubclassedUpdateManager::.ctor constructs UpdateManager(System.String", StringComparison.Ordinal));
    }

    /// <summary>Deliberate violations and compliant patterns. Never executed.</summary>
    internal static partial class Samples
    {
        /// <summary>An UpdateManager built through a subclass, with Velopack's own source (the rule must find it).</summary>
        internal sealed class SubclassedUpdateManager : Velopack.UpdateManager
        {
            internal SubclassedUpdateManager()
                : base("unused-path")
            {
            }
        }

        internal static partial class StrayWindowStyling
        {
            [LibraryImport("dwmapi")]
            internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
        }

        internal static partial class Compliant
        {
            internal static nint Open(uint pid) => OpenProcess(0x1000, false, pid);

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

        internal static partial class WrongMask
        {
            internal static nint Open(uint pid) => OpenProcess(0x1F0FFF, false, pid);

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

        internal static partial class MaskFromField
        {
            private static readonly uint s_mask = 0x1F0FFF;

            internal static nint Open(uint pid) => OpenProcess(s_mask, false, pid) + 0x1000;

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

        internal static partial class MaskFromParameter
        {
            internal static nint Open(uint mask, uint pid) => OpenProcess(mask, false, pid);

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

        internal static partial class NegativeMask
        {
            internal static nint Open(uint pid) => OpenProcess(0xFFFFFFFF, false, pid);

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

        internal static partial class NameLooksGenerated
        {
            internal static nint Evil__PInvoke(uint pid) => OpenProcess(0x1F0FFF, false, pid);

            [LibraryImport("kernel32.dll")]
            private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        }

#pragma warning disable SYSLIB1054 // Deliberately a classic DllImport: the rule must cover both styles.
        internal static class DirectDllImport
        {
            internal static nint Open(uint pid) => OpenProcess(0x1F0FFF, 0, pid);

            [DllImport("kernel32.dll")]
            private static extern nint OpenProcess(uint access, int inherit, uint pid);
        }

        [ComImport]
        [Guid("00000000-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface StrayComInterface
        {
            void Poke();
        }

        internal static class Banned
        {
            [DllImport("kernel32.dll")]
            internal static extern int ReadProcessMemory(nint process, nint address, nint buffer, nint size, out nint read);

            [DllImport("user32.dll")]
            internal static extern short GetKeyState(int key);

            [DllImport("evil.dll")]
            internal static extern int CloseHandle(nint handle);
        }

        public static class PublicInterop
        {
            [DllImport("kernel32.dll")]
            public static extern int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
        }
#pragma warning restore SYSLIB1054

        internal static class Dynamic
        {
            internal static nint Export(nint lib) => NativeLibrary.GetExport(lib, "ReadProcessMemory");

            internal static Action Wrap(nint fn) => Marshal.GetDelegateForFunctionPointer<Action>(fn);

            internal static unsafe int Call(delegate* unmanaged<int> fn) => fn();

            internal static object? Load(string path) => System.Reflection.Assembly.LoadFrom(path).CreateInstance("X");

            internal static object? Create(Type type) => Activator.CreateInstance(type);
        }

        internal static class ProcessUse
        {
            internal static DateTime Start(System.Diagnostics.Process p) => p.StartTime;

            internal static object? Module(System.Diagnostics.Process p) => p.MainModule;

            internal static System.Diagnostics.Process? Launch(string path) => System.Diagnostics.Process.Start(path);

            internal static void Stop(System.Diagnostics.Process p) => p.Kill(true);

            internal static System.Diagnostics.Process? OneStart(System.Diagnostics.ProcessStartInfo info) => System.Diagnostics.Process.Start(info);

            internal static void TwoStarts(System.Diagnostics.ProcessStartInfo a, System.Diagnostics.ProcessStartInfo b)
            {
                System.Diagnostics.Process.Start(a)?.Dispose();
                System.Diagnostics.Process.Start(b)?.Dispose();
            }

            internal static int Wait(System.Diagnostics.Process p)
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

#pragma warning disable CA1416 // Windows-only COM lookups: the sample only needs the call sites.
        internal static class LateBound
        {
            internal static object Invoke(System.Reflection.ConstructorInfo c) => c.Invoke(null);

            internal static object? InvokeMethod(System.Reflection.MethodInfo m) => m.Invoke(null, null);

            internal static object? ReadField(System.Reflection.FieldInfo f, object o) => f.GetValue(o);

            internal static void WriteField(System.Reflection.FieldInfo f, object o) => f.SetValue(o, null);

            internal static object? ReadProperty(System.Reflection.PropertyInfo p, object o) => p.GetValue(o);

            internal static void WriteProperty(System.Reflection.PropertyInfo p, object o) => p.SetValue(o, null);

            internal static Type? AssemblyType(System.Reflection.Assembly a) => a.GetType("X");

            internal static object? AssemblyCreate(System.Reflection.Assembly a) => a.CreateInstance("X");

            internal static Type? TypeByName() => Type.GetType("X");

            internal static Type? ByProgId() => Type.GetTypeFromProgID("X");

            internal static Type? ByClsid() => Type.GetTypeFromCLSID(Guid.Empty);

            internal static object? ActivateByName() => Activator.CreateInstance("asm", "X");

            internal static object? ActivateFrom() => Activator.CreateInstanceFrom("asm", "X");

            internal static System.Reflection.Assembly DomainLoad() => AppDomain.CurrentDomain.Load("X");

            internal static object? DomainCreate() => AppDomain.CurrentDomain.CreateInstance("asm", "X");

            internal static int DomainExecute() => AppDomain.CurrentDomain.ExecuteAssembly("x.exe");

            internal static System.Reflection.FieldInfo? RuntimeLookup() => System.Reflection.RuntimeReflectionExtensions.GetRuntimeField(typeof(int), "m_value");

            internal static object Moniker() => Marshal.BindToMoniker("x");

            internal static object? Threads(System.Diagnostics.Process p) => p.Threads;
        }
#pragma warning restore CA1416

        internal static class Gaps
        {
            internal static object? Invoke(Delegate d) => d.DynamicInvoke();

            internal static Action Bind(System.Reflection.MethodInfo m) => m.CreateDelegate<Action>();

            internal static Delegate StaticBind(System.Reflection.MethodInfo m) => Delegate.CreateDelegate(typeof(Action), m);

            internal static Delegate CompileLambda(System.Linq.Expressions.LambdaExpression e) => e.Compile();

            internal static Func<int> CompileTyped(System.Linq.Expressions.Expression<Func<int>> e) => e.Compile();

            internal static System.Reflection.Assembly LoadContext(string path) =>
                System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

            internal static object Emit() => new System.Reflection.Emit.DynamicMethod("x", typeof(void), Type.EmptyTypes);
        }

#pragma warning disable CA1416 // Windows-only members: the sample only needs the call sites.
        internal static class StartInfo
        {
            internal static System.Diagnostics.ProcessStartInfo Elevated() => new("x.exe")
            {
                Verb = "runas",
                UserName = "someone",
                Password = null,
                Domain = "d",
                UseShellExecute = true,
            };

            internal static System.Diagnostics.ProcessStartInfo Plain() => new("x.exe") { CreateNoWindow = true };
        }
#pragma warning restore CA1416

        internal static class UriCalls
        {
            internal static bool FromBase(Uri baseUri) => Uri.TryCreate(baseUri, "x", out _);

            internal static bool FromText(string text) => Uri.TryCreate(text, UriKind.Absolute, out _);
        }

        internal static class Network
        {
            internal static Type[] Types() =>
                [typeof(System.Net.Http.HttpClient), typeof(System.Net.NetworkInformation.Ping), typeof(System.Net.Security.SslStream)];

            internal static Type[] Forbidden() => [typeof(System.Net.WebSockets.ClientWebSocket), typeof(System.Net.HttpListener)];
        }

#pragma warning disable CA5359, CA5400 // Deliberate violations: the certificate rule must flag these.
        internal static class CertCallbacks
        {
            internal static System.Net.Http.HttpClientHandler Lax() => new()
            {
                ServerCertificateCustomValidationCallback = System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };

            internal static System.Net.Security.SslClientAuthenticationOptions Options() => new()
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }
#pragma warning restore CA5359, CA5400

        internal static class Pipes
        {
            internal static System.IO.Pipes.NamedPipeClientStream Local() => new(
                ".",
                "discord-ipc-0",
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly,
                System.Security.Principal.TokenImpersonationLevel.Identification);

            internal static System.IO.Pipes.NamedPipeClientStream Remote() => new(
                "remotehost",
                "discord-ipc-0",
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly,
                System.Security.Principal.TokenImpersonationLevel.Identification);

            internal static System.IO.Pipes.NamedPipeClientStream LegacyFourArgument() => new(
                ".", "discord-ipc-0", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

            internal static System.IO.Pipes.NamedPipeClientStream WithoutCurrentUserOnly() => new(
                ".",
                "discord-ipc-0",
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);

            internal static System.IO.Pipes.NamedPipeClientStream ImpersonationAllowed() => new(
                ".",
                "discord-ipc-0",
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly,
                System.Security.Principal.TokenImpersonationLevel.Impersonation);
        }
    }
}
