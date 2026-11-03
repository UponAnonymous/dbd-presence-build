namespace DbdPresence.Study;

/// <summary>Reads the pixel size of a PNG or JPEG from its header, so a picture larger than a request may carry is skipped instead of resized.</summary>
internal static class ImageSize
{
    /// <summary>Reads the size and the media type.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="mediaType"><c>image/png</c> or <c>image/jpeg</c>.</param>
    /// <returns>False when the bytes are neither a PNG nor a JPEG with a readable header.</returns>
    public static bool TryRead(ReadOnlySpan<byte> data, out int width, out int height, out string mediaType)
    {
        width = 0;
        height = 0;
        mediaType = string.Empty;
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R')
        {
            width = (int)ReadUInt32(data, 16);
            height = (int)ReadUInt32(data, 20);
            mediaType = "image/png";
            return width > 0 && height > 0;
        }

        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return false;
            }

            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            var length = (data[i + 2] << 8) | data[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                mediaType = "image/jpeg";
                return width > 0 && height > 0;
            }

            if (length < 2)
            {
                return false;
            }

            i += 2 + length;
        }

        return false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
}
