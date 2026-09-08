using Microsoft.UI.Xaml;

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
}
