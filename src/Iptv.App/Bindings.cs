using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Iptv.App;

/// <summary>
/// Binding helpers for types the view models deliberately do not know about.
/// </summary>
/// <remarks>
/// <see cref="Visibility"/> is a XAML type. Having a view model expose it would drag the
/// presentation assembly back into a project that cannot be tested without the Windows
/// workload, so the conversion happens here, at the binding, instead.
/// </remarks>
public static class Bindings
{
    /// <summary>Collapsed rather than hidden: a zero-width bar still takes its row height.</summary>
    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>How wide a poster is decoded, in pixels.</summary>
    /// <remarks>
    /// Twice the 120px tile, so the image is still sharp on a 200% display and nowhere
    /// near the several megapixels some providers serve. Without this every tile decodes
    /// at full size, and a screenful of them is hundreds of megabytes of bitmap for
    /// artwork drawn at the size of a thumbnail.
    /// </remarks>
    private const int PosterDecodeWidth = 240;

    /// <summary>
    /// Turns a checked artwork URL into something an Image can show.
    /// </summary>
    /// <remarks>
    /// The row has already rejected anything that is not absolute http or https, so this
    /// only has to build the bitmap. A URL that turns out to be a 404 or not an image
    /// leaves the Image transparent, which is deliberate: the placeholder initial sits
    /// behind it and shows through without needing a failure handler.
    /// </remarks>
    public static ImageSource? Poster(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new BitmapImage(uri) { DecodePixelWidth = PosterDecodeWidth };
    }
}
