using System.Net;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

/// <summary>
/// Adding, testing and syncing a provider.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="XtreamClient"/> over a fake transport rather than a
/// stubbed interface. Every provider bug in this project so far has been in the JSON — a
/// quoted number, a null where a list was expected, an object keyed by season — and a stub
/// would leave exactly that unexercised.
/// </remarks>
public sealed class ProvidersViewModelTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"zap-providers-{Guid.NewGuid():N}");

    private readonly SqliteConnectionFactory _factory;

    private string _response = "{}";
    private HttpStatusCode _status = HttpStatusCode.OK;

    public ProvidersViewModelTests()
        => _factory = new SqliteConnectionFactory(Path.Combine(_directory, "library.db"), pooled: false);

    /// <summary>Reversible without DPAPI, so these run anywhere.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[]? Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    private sealed class CannedHandler(Func<(HttpStatusCode Status, string Body)> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (status, body) = respond();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private ProvidersViewModel Create() => new(
        _factory,
        new FakeProtector(),
        credentials => new XtreamClient(
            new HttpClient(new CannedHandler(() => (_status, _response))),
            credentials));

    private static XtreamCredentials Credentials(string host = "http://a.invalid")
        => new(new Uri(host), "ACCT7X2", "SECRET99");

    private const string ActiveAccount =
        """
        {"user_info":{"auth":1,"status":"Active","max_connections":"2","exp_date":"1900000000"}}
        """;

    [Fact]
    public async Task A_new_install_lists_no_providers()
    {
        // The first-run state the PRD requires the app to explain, rather than showing an
        // empty channel list that reads as a broken sync.
        Assert.Empty(await Create().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_good_test_reports_the_connection_limit_and_the_expiry()
    {
        _response = ActiveAccount;

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.MaxConnections);
        Assert.Contains("2 connections", result.Message, StringComparison.Ordinal);
        Assert.Contains("expires", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_connection_account_is_called_out()
    {
        _response =
            """
            {"user_info":{"auth":1,"status":"Active","max_connections":"1"}}
            """;

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        // It changes how the app behaves — channel changes are paced and prebuffering is
        // impossible — so it is said rather than left for the user to discover.
        Assert.True(result.Ok);
        Assert.Contains("paced", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_credentials_say_so_rather_than_failing_vaguely()
    {
        _response = """{"user_info":{"auth":0}}""";

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("rejected the credentials", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_account_is_distinguished_from_a_wrong_password()
    {
        // auth:1 with a non-Active status is a live account that cannot stream. Reporting
        // it as a bad password would send the user to change something that is correct.
        _response = """{"user_info":{"auth":1,"status":"Expired"}}""";

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Expired", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_html_error_page_served_with_a_200_is_explained()
    {
        // The most common failure against a wrong host: a captive portal or an error page
        // with a success status. "Not JSON" is useless; naming the cause is not.
        _response = "<html><body>Not found</body></html>";

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTML error page", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_test_never_leaks_the_credentials()
    {
        _status = HttpStatusCode.InternalServerError;

        var result = await Create().TestAsync(Credentials(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.DoesNotContain("ACCT7X2", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET99", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_stores_nothing()
    {
        _response = ActiveAccount;

        var model = Create();
        await model.TestAsync(Credentials(), CancellationToken.None);

        // A provider row whose credentials have never worked is worse than no row: every
        // later failure then looks like a different problem.
        Assert.Empty(await model.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Adding_then_listing_shows_the_provider()
    {
        _response = ActiveAccount;

        var model = Create();
        var id = await model.AddAsync("Main", Credentials(), 0, CancellationToken.None);

        var provider = Assert.Single(await model.ListAsync(CancellationToken.None));
        Assert.Equal(id, provider.Id);
        Assert.True(provider.HasCredentials);
        Assert.True(provider.Enabled);
    }

    [Fact]
    public async Task Syncing_without_readable_credentials_says_what_to_do()
    {
        _response = ActiveAccount;

        var model = Create();
        var id = await model.AddAsync("Main", Credentials(), 0, CancellationToken.None);

        await using (var connection = await _factory.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE providers SET password = NULL WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.SyncAsync(id, null, CancellationToken.None));

        Assert.Contains("Re-enter", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_and_deleting_do_what_they_say()
    {
        _response = ActiveAccount;

        var model = Create();
        var id = await model.AddAsync("Main", Credentials(), 0, CancellationToken.None);

        await model.SetEnabledAsync(id, false, CancellationToken.None);
        Assert.False(Assert.Single(await model.ListAsync(CancellationToken.None)).Enabled);

        await model.DeleteAsync(id, CancellationToken.None);
        Assert.Empty(await model.ListAsync(CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory is disposable either way.
        }
    }
}
