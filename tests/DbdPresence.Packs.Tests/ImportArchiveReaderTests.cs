// Covers: FR-PACK-07 (hardened in-memory import: name rules, symlinks, limits counted by bytes actually read,
// PNG/JPEG/WebP by magic bytes, SVG rejected, mapping proposals that always need user confirmation).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DbdPresence.Packs.Import;

namespace DbdPresence.Packs.Tests;

public sealed class ImportArchiveReaderTests
{
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] s_jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    private static readonly byte[] s_webp = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBP"u8, 1, 2];

    private static ImportResult Read(MemoryStream zip, ImportLimits? limits = null) => new ImportArchiveReader(TestRoster.Embedded, limits).Read(zip);

    private static MemoryStream Zip(params (string Name, byte[] Data)[] entries) =>
        Zip(CompressionLevel.Optimal, entries);

    private static MemoryStream Zip(CompressionLevel level, params (string Name, byte[] Data)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                using var s = entry.Open();
                s.Write(data);
            }
        }

        return stream;
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("a/../../evil.png")]
    [InlineData("..\\evil.png")]
    [InlineData("C:/evil.png")]
    [InlineData("c:evil.png")]
    [InlineData("/etc/evil.png")]
    [InlineData("\\evil.png")]
    [InlineData("stream.png:hidden")]
    public void UnsafeNames_AreSkippedWithAReason(string name)
    {
        var result = Read(Zip((name, s_png), ("good.png", s_png)));

        Assert.False(result.IsRejected);
        Assert.Equal(["good.png"], result.Candidates.Select(c => c.DisplayName));
        Assert.Single(result.Skipped);
        Assert.False(string.IsNullOrWhiteSpace(result.Skipped[0].Reason));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.png")]
    [InlineData("art/NUL.png")]
    [InlineData("PRN.tar.png")]
    [InlineData("aux")]
    [InlineData("COM1.png")]
    [InlineData("com9.png")]
    [InlineData("LPT1.png")]
    [InlineData("lpt9")]
    public void ReservedDeviceNames_AreSkipped(string name)
    {
        var result = Read(Zip((name, s_png)));

        Assert.Empty(result.Candidates);
        Assert.Contains("device", Assert.Single(result.Skipped).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLikeNamesThatAreNotReserved_AreAccepted()
    {
        var result = Read(Zip(("console.png", s_png), ("COM10.png", s_png)));

        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void SymlinkEntry_IsSkipped()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = zip.CreateEntry("link.png");
            link.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using (var s = link.Open())
            {
                s.Write(s_png);
            }

            var regular = zip.CreateEntry("regular.png");
            regular.ExternalAttributes = (0x8000 | 0x1A4) << 16;
            using var r = regular.Open();
            r.Write(s_png);
        }

        var result = Read(stream);

        Assert.Equal(["regular.png"], result.Candidates.Select(c => c.DisplayName));
        Assert.Contains("link", Assert.Single(result.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZipBomb_SmallArchiveInflatingPastTheCaps_IsSkippedAfterReadingOnlyTheCap()
    {
        // 64 MB of zeros deflates to roughly 64 KB; reading stops at the 2 MB cap instead of inflating it all.
        var bomb = new byte[64 * 1024 * 1024];
        var stream = Zip(("bomb.png", [.. s_png, .. bomb]), ("ok.png", s_png));
        Assert.True(stream.Length < 1024 * 1024);

        var result = Read(stream);

        Assert.Equal(["ok.png"], result.Candidates.Select(c => c.DisplayName));
        Assert.Contains("Larger than", Assert.Single(result.Skipped).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HighRatioEntryUnderTheSizeCap_IsRefusedAsAZipBomb()
    {
        // 1.5 MB of zeros deflates to about 1.5 KB: a ratio near 1000:1, above the 100:1 limit.
        var data = new byte[1536 * 1024];
        s_png.CopyTo(data, 0);

        var result = Read(Zip(("squash.png", data)));

        Assert.True(result.IsRejected);
        Assert.Empty(result.Candidates);
        Assert.Contains("bomb", result.RejectedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntryOverTheSizeCap_WithANormalRatio_IsSkippedNotImported()
    {
        // Incompressible data: ratio ~1, so only the 2 MB cap applies.
        var data = new byte[(2 * 1024 * 1024) + 10];
        new Random(7).NextBytes(data);
        s_png.CopyTo(data, 0);

        var result = Read(Zip(CompressionLevel.NoCompression, ("big.png", data), ("ok.png", s_png)));

        Assert.Equal(["ok.png"], result.Candidates.Select(c => c.DisplayName));
        Assert.Contains("Larger than", Assert.Single(result.Skipped).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LyingDeclaredLength_DoesNotLetAnOversizeEntryThrough()
    {
        var data = new byte[(3 * 1024 * 1024)];
        new Random(11).NextBytes(data);
        s_png.CopyTo(data, 0);
        var stream = Zip(CompressionLevel.NoCompression, ("lie.png", data));
        PatchDeclaredUncompressedSize(stream, 12);

        var result = Read(stream);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ArchiveOverTheArchiveCap_IsRefused()
    {
        var stream = Zip(("a.png", s_png));
        var result = Read(stream, new ImportLimits { MaxArchiveBytes = stream.Length - 1 });

        Assert.True(result.IsRejected);
    }

    [Fact]
    public void TooManyEntries_IsRefused()
    {
        var entries = Enumerable.Range(0, 501).Select(i => ($"img{i}.png", s_png)).ToArray();

        var result = Read(Zip(entries));

        Assert.True(result.IsRejected);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ExactlyTheEntryLimit_IsAccepted()
    {
        var entries = Enumerable.Range(0, 500).Select(i => ($"img{i}.png", s_png)).ToArray();

        var result = Read(Zip(entries));

        Assert.False(result.IsRejected);
        Assert.Equal(500, result.Candidates.Count);
    }

    [Fact]
    public void SkippedOverSizeEntries_CountTowardTheTotalBudget_SoInflateWorkIsBounded()
    {
        // Threat-model review I-5: each over-size entry is cut at the per-entry cap, but the bytes inflated up to the cut still use the total budget.
        var big = new byte[1200];
        new Random(5).NextBytes(big);
        s_png.CopyTo(big, 0);
        var entries = Enumerable.Range(0, 6).Select(i => ($"big{i}.png", big)).ToArray();

        var result = Read(Zip(CompressionLevel.NoCompression, entries), new ImportLimits { MaxEntryBytes = 1000, MaxTotalBytes = 2500 });

        Assert.True(result.IsRejected);
        Assert.Contains("expands to more than", result.RejectedReason, StringComparison.Ordinal);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void SkippedOverSizeEntries_WithinTheTotalBudget_AreStillJustSkipped()
    {
        var big = new byte[1200];
        new Random(5).NextBytes(big);
        var ok = new byte[100];
        s_png.CopyTo(ok, 0);

        var result = Read(Zip(CompressionLevel.NoCompression, ("big.png", big), ("ok.png", ok)), new ImportLimits { MaxEntryBytes = 1000, MaxTotalBytes = 2500 });

        Assert.False(result.IsRejected);
        Assert.Equal(["ok.png"], result.Candidates.Select(c => c.DisplayName));
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void TotalOverTheTotalCap_IsRefused()
    {
        var data = new byte[600];
        new Random(3).NextBytes(data);
        s_png.CopyTo(data, 0);

        var result = Read(Zip(CompressionLevel.NoCompression, ("a.png", data), ("b.png", data)), new ImportLimits { MaxTotalBytes = 1000 });

        Assert.True(result.IsRejected);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void NotAZip_IsRefused()
    {
        var result = Read(new MemoryStream(Encoding.ASCII.GetBytes("this is not a zip file at all")));

        Assert.True(result.IsRejected);
    }

    [Fact]
    public void Svg_AndOtherFormats_AreSkippedWithReasons()
    {
        var svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        var result = Read(Zip(("logo.svg", svg), ("anim.gif", "GIF89a...."u8.ToArray()), ("notes.txt", "hello"u8.ToArray()), ("fake.png", svg)));

        Assert.Empty(result.Candidates);
        Assert.Equal(4, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        Assert.Contains(result.Skipped, s => s.Reason.Contains("SVG", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidPngJpegAndWebp_AreAcceptedByMagicBytesNotExtension()
    {
        var result = Read(Zip(("a.dat", s_png), ("b.png", s_jpeg), ("c.bin", s_webp)));

        Assert.Equal(
            [ImportImageFormat.Png, ImportImageFormat.Jpeg, ImportImageFormat.WebP],
            result.Candidates.Select(c => c.Format));
        Assert.Equal(s_png, result.Candidates[0].Bytes);
    }

    [Fact]
    public void DisplayNames_AreSanitisedAndNeverContainPathParts()
    {
        var result = Read(Zip(("folder/sub/na<me>|x.png", s_png)));

        Assert.Equal("na_me__x.png", Assert.Single(result.Candidates).DisplayName);
    }

    [Theory]
    [InlineData("The Trapper.png", ProposalKind.Character, "the-trapper", "character:the-trapper")]
    [InlineData("trapper_portrait.png", ProposalKind.Character, "the-trapper", "character:the-trapper")]
    [InlineData("Hawkins.webp", ProposalKind.Map, "hawkins", "map:hawkins")]
    [InlineData("killer.jpg", ProposalKind.Role, "killer", "role:killer")]
    [InlineData("Survivor-Dwight-Fairfield.png", ProposalKind.Character, "dwight-fairfield", "character:dwight-fairfield")]
    public void MappingProposals_AreSuggestedAndAlwaysNeedConfirmation(string file, ProposalKind kind, string slug, string key)
    {
        var proposal = MappingProposer.Propose(file, TestRoster.Embedded);

        Assert.NotNull(proposal);
        Assert.Equal(kind, proposal.Kind);
        Assert.Equal(slug, proposal.Slug);
        Assert.Equal(key, proposal.Key);
        Assert.True(proposal.NeedsUserConfirmation);
    }

    [Theory]
    [InlineData("random-art.png")]
    [InlineData("killers-pack-cover.png")]
    [InlineData("12345.png")]
    public void UnknownNames_ProposeNothing(string file) => Assert.Null(MappingProposer.Propose(file, TestRoster.Embedded));

    [Fact]
    public void ReaderOutput_CarriesTheProposalOnCandidates()
    {
        var result = Read(Zip(("hawkins.png", s_png), ("misc.png", s_png)));

        Assert.True(result.Candidates[0].Proposal?.NeedsUserConfirmation);
        Assert.Null(result.Candidates[1].Proposal);
    }

    private static void PatchDeclaredUncompressedSize(MemoryStream stream, uint declared)
    {
        var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        var patched = 0;
        for (var i = 0; i + 30 < bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i + 22, 4), declared);
                patched++;
            }
            else if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(i + 24, 4), declared);
                patched++;
            }
        }

        Assert.Equal(2, patched);
    }
}
