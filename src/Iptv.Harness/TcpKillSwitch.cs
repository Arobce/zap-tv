using System.Net;
using System.Net.Sockets;

namespace Iptv.Harness;

/// <summary>
/// A local TCP forwarder that can be cut, standing in for a firewall rule.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 8 exit criterion says "block the host in the firewall". Creating a firewall
/// rule needs elevation, leaves state on the machine if the run dies partway, and cannot be
/// run by anyone checking this criterion on their own machine without the same rights.
/// </para>
/// <para>
/// This produces the same failure instead: the live connection is severed mid-transfer and
/// nothing accepts a new one. That shape is the point. A dead URL - which the existing
/// drill uses - fails at connect, which mpv reports promptly and differently; a stream cut
/// while bytes are flowing just stops, and detecting <em>that</em> is what the stall
/// watcher exists for and what has never been tested against a real provider.
/// </para>
/// </remarks>
internal sealed class TcpKillSwitch : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<TcpClient> _live = [];
    private readonly Lock _gate = new();

    private volatile bool _killed;

    private TcpKillSwitch(TcpListener listener, string targetHost, int targetPort)
    {
        _listener = listener;
        _targetHost = targetHost;
        _targetPort = targetPort;

        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The loopback port to point a client at.</summary>
    public int Port { get; }

    /// <summary>Bytes forwarded to the client, as a sign it is genuinely carrying a stream.</summary>
    public long BytesForwarded;

    /// <summary>
    /// The first response line the upstream sent, for diagnosing what it did.
    /// </summary>
    /// <remarks>
    /// Xtream servers frequently answer a stream request with a redirect to a different
    /// host, and a client that follows one leaves this forwarder behind entirely — it then
    /// looks like a working drill that cuts nothing. Recording the status line is what
    /// tells those two apart.
    /// </remarks>
    public string? FirstStatusLine { get; private set; }

    public static TcpKillSwitch Start(string targetHost, int targetPort)
    {
        // Loopback and port 0: the OS picks a free port, and nothing outside this machine
        // can reach the forwarder even briefly.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var forwarder = new TcpKillSwitch(listener, targetHost, targetPort);

        _ = Task.Run(forwarder.AcceptLoopAsync);

        return forwarder;
    }

    /// <summary>
    /// Cuts everything, as a firewall rule would.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Closing the live sockets is what mpv experiences as the stream
    /// stopping; refusing new ones is what stops it quietly reconnecting and hiding the
    /// failure the drill is trying to measure.
    /// </remarks>
    public void Kill()
    {
        _killed = true;

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already down.
        }

        lock (_gate)
        {
            foreach (var client in _live)
            {
                try
                {
                    // Reset rather than a graceful close: a FIN reads as end-of-stream, and
                    // mpv would treat that as the file ending rather than as a failure.
                    client.Client.Close(0);
                }
                catch (Exception)
                {
                    // Every one of these is already broken. That is the point.
                }
            }

            _live.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_killed && !_shutdown.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ForwardAsync(client));
        }
    }

    private async Task ForwardAsync(TcpClient inbound)
    {
        TcpClient? outbound = null;

        try
        {
            outbound = new TcpClient();
            await outbound.ConnectAsync(_targetHost, _targetPort, _shutdown.Token).ConfigureAwait(false);

            Track(inbound);
            Track(outbound);

            var upstream = PumpAsync(inbound, outbound, count: false);
            var downstream = PumpAsync(outbound, inbound, count: true);

            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A forwarder that throws here has already lost the connection it was carrying,
            // which is indistinguishable from what Kill does on purpose.
        }
        finally
        {
            Untrack(inbound);
            Untrack(outbound);

            inbound.Dispose();
            outbound?.Dispose();
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to, bool count)
    {
        var buffer = new byte[64 * 1024];

        var source = from.GetStream();
        var sink = to.GetStream();

        while (!_killed)
        {
            var read = await source.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await sink.WriteAsync(buffer.AsMemory(0, read), _shutdown.Token).ConfigureAwait(false);

            if (count)
            {
                if (FirstStatusLine is null)
                {
                    RecordStatusLine(buffer.AsSpan(0, read));
                }

                Interlocked.Add(ref BytesForwarded, read);
            }
        }
    }

    /// <summary>Keeps the status line and the Location header, if the response has one.</summary>
    /// <remarks>
    /// Only the first response, and only its head. The body is a video stream and holding
    /// any of it would be pointless; the credentials live in the request rather than the
    /// response, so nothing kept here needs scrubbing.
    /// </remarks>
    private void RecordStatusLine(ReadOnlySpan<byte> head)
    {
        var text = System.Text.Encoding.ASCII.GetString(
            head[..Math.Min(head.Length, 1024)]);

        if (!text.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return;
        }

        var lines = text.Split('\n');
        var status = lines[0].Trim();

        foreach (var line in lines)
        {
            if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var target))
            {
                // Host only. A redirect target can carry the credentials forward in its
                // path, and this string is printed.
                FirstStatusLine = $"{status} -> {target.Host}:{target.Port}";
                return;
            }
        }

        FirstStatusLine = status;
    }

    private void Track(TcpClient client)
    {
        lock (_gate)
        {
            _live.Add(client);
        }
    }

    private void Untrack(TcpClient? client)
    {
        if (client is null)
        {
            return;
        }

        lock (_gate)
        {
            _live.Remove(client);
        }
    }

    public void Dispose()
    {
        Kill();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
