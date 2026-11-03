namespace DbdPresence.Platform.Windows.Tests.Capture;

/// <summary>
/// The capture and OCR tests show windows of this test process, and the capture source picks the largest visible window the
/// process owns (FR-CAP-06). xUnit runs different test classes in parallel, so without one shared collection a test could capture
/// another test's window, or find a window where it expects none. Tests in one collection run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OwnWindowCaptureGroup
{
    /// <summary>The collection name used by every test class that shows a <see cref="CaptureTestWindow"/>.</summary>
    public const string Name = "Capture of this process's own windows";
}
