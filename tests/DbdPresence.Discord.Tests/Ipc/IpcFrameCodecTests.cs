// Covers: FR-RPC-01 (frame layout, single-buffer frames, 64 KiB frame cap incl. header, opcodes).
using System.Text;
using DbdPresence.Discord.Ipc;
using DbdPresence.Discord.Tests.Support;

namespace DbdPresence.Discord.Tests.Ipc;

public sealed class IpcFrameCodecTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Header(uint opcode, uint length) =>
    [
        .. BitConverter.GetBytes(opcode),
        .. BitConverter.GetBytes(length),
    ];

    [Fact]
    public void Encode_WritesLittleEndianOpcodeAndLengthThenPayload()
    {
        var frame = IpcFrameCodec.Encode(IpcOpcode.Frame, Utf8("{}"));

        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, (byte)'{', (byte)'}' }, frame);
    }

    [Fact]
    public void Encode_LengthUsesAllFourBytesLittleEndian()
    {
        var frame = IpcFrameCodec.Encode(IpcOpcode.Pong, new byte[0x0102]);

        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00 }, frame[..8]);
        Assert.Equal(8 + 0x0102, frame.Length);
    }

    [Theory]
    [InlineData("Handshake", 0u)]
    [InlineData("Frame", 1u)]
    [InlineData("Close", 2u)]
    [InlineData("Ping", 3u)]
    [InlineData("Pong", 4u)]
    public void Encode_UsesTheDocumentedOpcodeValues(string name, uint expected)
    {
        var frame = IpcFrameCodec.Encode(Enum.Parse<IpcOpcode>(name), []);

        Assert.Equal(expected, BitConverter.ToUInt32(frame, 0));
        Assert.Equal(IpcFrameCodec.HeaderSize, frame.Length);
    }

    [Fact]
    public void Encode_AcceptsA64KiBFrameIncludingItsHeader_AndRejectsMore()
    {
        Assert.Equal(65528, IpcFrameCodec.MaxPayloadBytes);
        Assert.Equal(65536, IpcFrameCodec.Encode(IpcOpcode.Frame, new byte[65528]).Length);
        Assert.Throws<ArgumentException>(() => IpcFrameCodec.Encode(IpcOpcode.Frame, new byte[65529]));
    }

    [Fact]
    public void Encode_RejectsUnknownOpcode() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IpcFrameCodec.Encode((IpcOpcode)9, []));

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public async Task RoundTrip_PreservesOpcodeAndPayload(uint rawOpcode)
    {
        var opcode = (IpcOpcode)rawOpcode;
        var payload = Utf8("""{"text":"héllo ✓"}""");
        await using var transport = new BufferTransport(IpcFrameCodec.Encode(opcode, payload));

        var frame = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(opcode, frame.Value.Opcode);
        Assert.Equal(payload, frame.Value.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Read_AssemblesFramesFromPartialReads(int chunkSize)
    {
        var first = IpcFrameCodec.Encode(IpcOpcode.Frame, Utf8("""{"a":1}"""));
        var second = IpcFrameCodec.Encode(IpcOpcode.Ping, Utf8("x"));
        await using var transport = new BufferTransport([.. first, .. second], chunkSize);

        var one = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);
        var two = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);
        var end = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);

        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(one!.Value.Payload.Span));
        Assert.Equal(IpcOpcode.Ping, two!.Value.Opcode);
        Assert.Null(end);
    }

    [Fact]
    public async Task Read_EmptyPayloadIsAValidFrame()
    {
        await using var transport = new BufferTransport(Header(3, 0));

        var frame = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);

        Assert.Equal(IpcOpcode.Ping, frame!.Value.Opcode);
        Assert.True(frame.Value.Payload.IsEmpty);
    }

    [Fact]
    public async Task Read_AcceptsAPayloadThatFillsA64KiBFrame()
    {
        await using var transport = new BufferTransport([.. Header(1, 65528), .. new byte[65528]], 4096);

        var frame = await IpcFrameCodec.ReadAsync(transport, CancellationToken.None);

        Assert.Equal(65528, frame!.Value.Payload.Length);
    }

    [Fact]
    public async Task Read_RejectsOversizeLengthBeforeReadingThePayload()
    {
        // Only the header is available: the codec must fail on the length alone, not wait for 65529 bytes.
        await using var transport = new BufferTransport(Header(1, 65529));

        var error = await Assert.ThrowsAsync<IpcProtocolException>(async () => await IpcFrameCodec.ReadAsync(transport, CancellationToken.None));

        Assert.Contains("65529", error.Message, StringComparison.Ordinal);
        Assert.Equal(IpcFrameCodec.HeaderSize, transport.Position);
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(255u)]
    [InlineData(0x01000000u)]
    public async Task Read_UnknownOpcodeIsAProtocolError(uint opcode)
    {
        await using var transport = new BufferTransport([.. Header(opcode, 0)]);

        await Assert.ThrowsAsync<IpcProtocolException>(async () => await IpcFrameCodec.ReadAsync(transport, CancellationToken.None));
    }

    [Fact]
    public async Task Read_CleanEndBetweenFramesReturnsNull()
    {
        await using var transport = new BufferTransport([]);

        Assert.Null(await IpcFrameCodec.ReadAsync(transport, CancellationToken.None));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8 + 2)]
    public async Task Read_EndInsideAFrameIsAProtocolError(int availableBytes)
    {
        var full = IpcFrameCodec.Encode(IpcOpcode.Frame, Utf8("""{"a":1}"""));
        await using var transport = new BufferTransport(full[..availableBytes]);

        await Assert.ThrowsAsync<IpcProtocolException>(async () => await IpcFrameCodec.ReadAsync(transport, CancellationToken.None));
    }
}
