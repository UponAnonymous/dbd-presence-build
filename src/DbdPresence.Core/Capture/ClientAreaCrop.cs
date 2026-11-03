namespace DbdPresence.Core.Capture;

/// <summary>
/// Finds where a window's client area sits inside a captured frame. Windows.Graphics.Capture delivers the window's visible frame,
/// which for a windowed game includes the title bar and the border (the owner's windowed recordings are 1922x1112 for a 1920x1080
/// game), so every normalised HUD region would be offset. This pure function takes the window metadata the safety allow-list already
/// permits (SAFETY_AND_PRIVACY section 2.1: visible frame bounds and the client rectangle in screen coordinates) and returns the
/// rectangle of the captured frame to keep. It never guesses: any inconsistency gives <see langword="null"/>.
/// </summary>
public static class ClientAreaCrop
{
    /// <summary>How many pixels the captured size may differ from the reported frame bounds and still count as the same size.</summary>
    public const int SizeTolerancePixels = 2;

    /// <summary>How far the horizontal and vertical scale may differ when the captured size is not the reported size (a process that is not DPI aware).</summary>
    public const double ScaleTolerance = 0.01;

    /// <summary>Computes the client-area rectangle inside the captured frame.</summary>
    /// <param name="frame">The window's visible frame in screen coordinates (what the capture shows).</param>
    /// <param name="client">The client area in screen coordinates.</param>
    /// <param name="capturedWidth">Pixel width of the captured frame.</param>
    /// <param name="capturedHeight">Pixel height of the captured frame.</param>
    /// <returns>The rectangle to keep, or null when the metadata and the captured size do not agree (the caller must not use the frame as client-area).</returns>
    public static PixelRect? Compute(ScreenRect frame, ScreenRect client, int capturedWidth, int capturedHeight)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || client.Width <= 0 || client.Height <= 0 || capturedWidth < 1 || capturedHeight < 1)
        {
            return null;
        }

        // The client rectangle must lie inside the visible frame; anything else means the two were read at different moments or in different spaces.
        if (client.Left < frame.Left || client.Top < frame.Top || client.Right > frame.Right + SizeTolerancePixels || client.Bottom > frame.Bottom + SizeTolerancePixels)
        {
            return null;
        }

        var scaleX = 1.0;
        var scaleY = 1.0;
        if (Math.Abs(capturedWidth - frame.Width) > SizeTolerancePixels || Math.Abs(capturedHeight - frame.Height) > SizeTolerancePixels)
        {
            scaleX = (double)capturedWidth / frame.Width;
            scaleY = (double)capturedHeight / frame.Height;
            if (Math.Abs(scaleX - scaleY) > ScaleTolerance)
            {
                return null;
            }
        }

        var left = (int)Math.Round((client.Left - frame.Left) * scaleX, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round((client.Top - frame.Top) * scaleY, MidpointRounding.AwayFromZero);
        var right = Math.Min((int)Math.Round((client.Right - frame.Left) * scaleX, MidpointRounding.AwayFromZero), capturedWidth);
        var bottom = Math.Min((int)Math.Round((client.Bottom - frame.Top) * scaleY, MidpointRounding.AwayFromZero), capturedHeight);
        return right - left < 1 || bottom - top < 1 ? null : new PixelRect(left, top, right - left, bottom - top);
    }
}
