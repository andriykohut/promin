using Promin.Protocol.Video;

namespace Promin.Master.Core.Control;

public static class ControlGeometry
{
    /// <summary>
    /// Maps a point in a view of <paramref name="viewWidth"/> by <paramref name="viewHeight"/> to the agent PC's
    /// screen, 0 to 65535 on each axis, for a <paramref name="picture"/> shown centred at the largest size that fits
    /// (the same fit as <see cref="FrameSizing.FitRect"/>). Null when the point is outside the picture.
    /// </summary>
    public static (ushort X, ushort Y)? ToRemote(double x, double y, double viewWidth, double viewHeight, PixelSize picture)
    {
        double width, height;
        if (picture.Width * viewHeight >= picture.Height * viewWidth)
        {
            width = viewWidth;
            height = picture.Height * viewWidth / picture.Width;
        }
        else
        {
            height = viewHeight;
            width = picture.Width * viewHeight / picture.Height;
        }

        var left = (viewWidth - width) / 2;
        var top = (viewHeight - height) / 2;
        if (x < left || x > left + width || y < top || y > top + height)
        {
            return null;
        }

        return (Scale((x - left) / width), Scale((y - top) / height));
    }

    private static ushort Scale(double fraction) =>
        (ushort)Math.Clamp(Math.Floor(fraction * 65536), 0, ushort.MaxValue);
}
