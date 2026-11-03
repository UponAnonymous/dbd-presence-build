// Covers: FR-LIFE-01 (the Store package starts with Windows through a manifest startup task), SAFETY_AND_PRIVACY section 2.1 (the package asks Windows for
// no more than two capabilities), ADR-0003 update 2026-11-02 (borderless capture needs graphicsCaptureWithoutBorder in a packaged app). The Microsoft Store
// (MSIX) manifest template in build/msix is checked here, on Linux, so a capability cannot be added or dropped without a failing test.
using System.Xml.Linq;

namespace DbdPresence.ArchitectureTests;

public sealed class MsixManifestTests
{
    private static readonly string s_msixDir = Path.Combine(ProductAssemblies.RepositoryRoot, "build", "msix");

    private static XDocument Manifest() => XDocument.Load(Path.Combine(s_msixDir, "AppxManifest.xml"));

    private static IEnumerable<XElement> Named(XElement parent, string localName) => parent.Descendants().Where(e => e.Name.LocalName == localName);

    [Fact]
    public void Capabilities_AreExactlyRunFullTrustAndGraphicsCaptureWithoutBorder()
    {
        var capabilities = Named(Manifest().Root!, "Capabilities").Single();

        var declared = capabilities.Elements().Select(e => $"{e.Name.LocalName}:{(string?)e.Attribute("Name")}").Order(StringComparer.Ordinal).ToArray();

        // Not graphicsCaptureProgrammatic (the app captures a window handle from a full-trust process) and no network or device capability.
        Assert.Equal(["Capability:graphicsCaptureWithoutBorder", "Capability:runFullTrust"], declared);
        Assert.Equal(
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities",
            capabilities.Elements().Single(e => (string?)e.Attribute("Name") == "runFullTrust").Name.NamespaceName);
        Assert.Equal(
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/11",
            capabilities.Elements().Single(e => (string?)e.Attribute("Name") == "graphicsCaptureWithoutBorder").Name.NamespaceName);
    }

    [Fact]
    public void TargetsWindowsDesktop_From10_0_19041_0()
    {
        var target = Named(Manifest().Root!, "TargetDeviceFamily").Single();

        Assert.Equal("Windows.Desktop", (string?)target.Attribute("Name"));
        Assert.Equal("10.0.19041.0", (string?)target.Attribute("MinVersion"));
        Assert.Equal("10.0.26100.0", (string?)target.Attribute("MaxVersionTested"));
    }

    [Fact]
    public void RunsDbdPresenceExe_AsAFullTrustApplication()
    {
        var application = Named(Manifest().Root!, "Application").Single();

        Assert.Equal("DBD-Presence.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)application.Attribute("EntryPoint"));
        var assemblyName = XDocument.Load(Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.App", "DbdPresence.App.csproj")).Descendants("AssemblyName").Single().Value;
        Assert.Equal(assemblyName + ".exe", (string?)application.Attribute("Executable"));
    }

    [Fact]
    public void DeclaresTheStartupTask_ThatTheAppAsksFor()
    {
        var extension = Named(Manifest().Root!, "Extension").Single(e => (string?)e.Attribute("Category") == "windows.startupTask");
        var task = Named(extension, "StartupTask").Single();

        Assert.Equal("DBD-Presence.exe", (string?)extension.Attribute("Executable"));
        Assert.Equal("DbdPresenceStartup", (string?)task.Attribute("TaskId"));
        // Off until the user turns it on (opt-in, FR-LIFE-01).
        Assert.Equal("false", (string?)task.Attribute("Enabled"));
        Assert.Equal("DBD Presence", (string?)task.Attribute("DisplayName"));
        var adapter = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, "src", "DbdPresence.Platform.Windows", "Startup", "PackagedStartupTask.cs"));
        Assert.Contains("TaskId = \"DbdPresenceStartup\"", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityAndPublisherAreParameters_AndTheVersionIsFilledByTheScript()
    {
        var identity = Named(Manifest().Root!, "Identity").Single();

        Assert.Equal("{{IDENTITY_NAME}}", (string?)identity.Attribute("Name"));
        Assert.Equal("{{PUBLISHER}}", (string?)identity.Attribute("Publisher"));
        Assert.Equal("{{VERSION}}", (string?)identity.Attribute("Version"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));
        Assert.Equal("{{PUBLISHER_DISPLAY_NAME}}", Named(Manifest().Root!, "PublisherDisplayName").Single().Value);
        Assert.Equal("DBD Presence", Named(Manifest().Root!, "DisplayName").First().Value);
        var script = File.ReadAllText(Path.Combine(ProductAssemblies.RepositoryRoot, "build", "pack-msix.ps1"));
        foreach (var token in new[] { "{{IDENTITY_NAME}}", "{{PUBLISHER}}", "{{PUBLISHER_DISPLAY_NAME}}", "{{VERSION}}" })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryLogoTheManifestNames_ExistsAtItsSize()
    {
        var logos = Manifest().Descendants().SelectMany(e => e.Attributes()).Select(a => a.Value).Where(v => v.StartsWith("Assets\\", StringComparison.Ordinal))
            .Concat(Named(Manifest().Root!, "Logo").Select(e => e.Value)).Distinct(StringComparer.Ordinal).ToArray();

        Assert.Equal(["Assets\\Square150x150Logo.png", "Assets\\Square44x44Logo.png", "Assets\\StoreLogo.png", "Assets\\Wide310x150Logo.png"], logos.Order(StringComparer.Ordinal).ToArray());
        var sizes = new Dictionary<string, (int W, int H)>
        {
            ["Square44x44Logo.png"] = (44, 44),
            ["Square150x150Logo.png"] = (150, 150),
            ["StoreLogo.png"] = (50, 50),
            ["Wide310x150Logo.png"] = (310, 150),
        };
        foreach (var (file, size) in sizes)
        {
            var bytes = File.ReadAllBytes(Path.Combine(s_msixDir, "Assets", file));
            // PNG: 8-byte signature, then IHDR whose width and height are big-endian 32-bit integers at offsets 16 and 20.
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
            Assert.Equal(size.W, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
            Assert.Equal(size.H, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        }
    }
}
