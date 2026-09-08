using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Iptv.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Iptv.App;

/// <summary>
/// The Providers panel: add, test, sync and manage the configured providers.
/// </summary>
/// <remarks>
/// A separate partial file. MainWindow is already the largest thing in the project, and
/// this surface has nothing to do with mpv, the swap chain or the key map.
/// </remarks>
public sealed partial class MainWindow
{
    private ProvidersViewModel? _providers;

    /// <summary>
    /// One handler for the process, per the PRD.
    /// </summary>
    /// <remarks>
    /// The default .NET User-Agent is rejected by some panels, and a handler per request
    /// would exhaust sockets on a sync that makes hundreds.
    /// </remarks>
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapTV/0.1");
        return client;
    }

    private ProvidersViewModel Providers => _providers ??= new ProvidersViewModel(
        new SqliteConnectionFactory(_databasePath),
        new DpapiSecretProtector(),
        credentials => new XtreamClient(Http, credentials));

    /// <summary>Shows the panel, refreshing what it lists.</summary>
    private async Task ShowProvidersAsync()
    {
        ProvidersPanel.Visibility = Visibility.Visible;
        await RefreshProvidersAsync();
    }

    private async Task RefreshProvidersAsync()
    {
        try
        {
            var providers = await Providers.ListAsync(CancellationToken.None);

            ProviderList.ItemsSource = providers
                .Select(p => ProviderCard.From(p, DateTimeOffset.UtcNow))
                .ToList();

            // The first-run case the PRD calls out. An empty channel list with no
            // explanation reads as a broken sync rather than as nothing configured.
            ProvidersIntro.Text = providers.Count == 0
                ? "No providers yet. Add one below. Test checks the credentials and reports "
                  + "what the account allows before anything is stored."
                : "Sync refreshes a whole library. Disable removes a provider from the lists "
                  + "and from failover without losing anything.";
        }
        catch (Exception exception)
        {
            Log($"listing providers failed: {exception}");
            ProviderResultText.Text = exception.Message;
        }
    }

    private XtreamCredentials? ReadForm()
    {
        var host = ProviderHostBox.Text.Trim();
        var user = ProviderUserBox.Text.Trim();
        var password = ProviderPassBox.Password;

        if (host.Length == 0 || user.Length == 0 || password.Length == 0)
        {
            ProviderResultText.Text = "Host, username and password are all required.";
            return null;
        }

        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Said before a request is made. "Could not reach the provider" for a typo in
            // the scheme sends the user looking at their network.
            ProviderResultText.Text = "The host must start with http:// or https://";
            return null;
        }

        return new XtreamCredentials(uri, user, password);
    }

    private async void OnTestProviderClicked(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } credentials)
        {
            return;
        }

        TestProviderButton.IsEnabled = false;
        ProviderResultText.Text = "Testing...";

        try
        {
            var result = await Providers.TestAsync(credentials, CancellationToken.None);
            ProviderResultText.Text = result.Message;
            Log($"provider test: ok={result.Ok} {result.Message}");
        }
        finally
        {
            TestProviderButton.IsEnabled = true;
        }
    }

    private async void OnAddProviderClicked(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } credentials)
        {
            return;
        }

        var name = ProviderNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = credentials.BaseUrl.Host;
        }

        AddProviderButton.IsEnabled = false;

        try
        {
            var id = await Providers.AddAsync(name, credentials, 0, CancellationToken.None);

            // Cleared as soon as it is stored. A password left in a box is a password on
            // screen for as long as the panel is open.
            ProviderPassBox.Password = string.Empty;
            ProviderResultText.Text = "Added. Syncing now, which moves the whole library.";

            await RefreshProvidersAsync();
            await SyncProviderAsync(id);
        }
        catch (Exception exception)
        {
            Log($"adding provider failed: {exception}");
            ProviderResultText.Text = CredentialScrubber.Scrub(exception.Message);
        }
        finally
        {
            AddProviderButton.IsEnabled = true;
        }
    }

    private async void OnSyncProviderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            await SyncProviderAsync(id);
        }
    }

    private async Task SyncProviderAsync(int providerId)
    {
        // Marshalled onto the UI thread by Progress: LibrarySync reports from whichever
        // thread the fetch is on, and this writes to a TextBlock.
        var progress = new Progress<SyncProgress>(update =>
            ProviderResultText.Text = $"{update.Stage}: {update.Detail}");

        try
        {
            var report = await Providers.SyncAsync(providerId, progress, CancellationToken.None);

            ProviderResultText.Text =
                $"Done in {report.Elapsed.TotalSeconds:F0}s · " +
                $"{report.Live.Added + report.Live.Updated:N0} channels · " +
                $"{report.Vod.Added + report.Vod.Updated:N0} films · " +
                $"{report.Series:N0} series · " +
                $"{report.LiveCategories + report.VodCategories:N0} categories";

            Log($"sync complete: {ProviderResultText.Text}");

            await RefreshProvidersAsync();

            // The library behind the panel is now different, so the list it is covering
            // is rebuilt rather than left showing what was there before.
            await ApplyAsync(await _browser.LoadAsync(CancellationToken.None));
            await LoadCategoriesAsync();
        }
        catch (Exception exception)
        {
            Log($"sync failed: {exception}");
            ProviderResultText.Text = CredentialScrubber.Scrub(exception.Message);
        }
    }

    private async void OnToggleProviderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id })
        {
            return;
        }

        var provider = (await Providers.ListAsync(CancellationToken.None))
            .FirstOrDefault(p => p.Id == id);

        if (provider is null)
        {
            return;
        }

        await Providers.SetEnabledAsync(id, !provider.Enabled, CancellationToken.None);
        await RefreshProvidersAsync();
        await ApplyAsync(await _browser.LoadAsync(CancellationToken.None));
    }

    private async void OnDeleteProviderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id })
        {
            return;
        }

        // Confirmed, because this cascades to every stream, series and health row the
        // provider brought. Disable is the reversible operation and is offered alongside.
        var confirm = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Remove this provider?",
            Content = "Its channels, films and series are deleted. Favourites and resume "
                      + "positions are kept. Disable instead if you only want it out of the way.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await Providers.DeleteAsync(id, CancellationToken.None);
        await RefreshProvidersAsync();
        await ApplyAsync(await _browser.LoadAsync(CancellationToken.None));
    }

    private async void OnOpenProvidersClicked(object sender, RoutedEventArgs e)
        => await ShowProvidersAsync();

    private void OnCloseProvidersClicked(object sender, RoutedEventArgs e)
    {
        ProvidersPanel.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Programmatic);
    }
}
