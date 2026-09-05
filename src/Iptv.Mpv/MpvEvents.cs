namespace Iptv.Mpv;

/// <summary>mpv event kinds. Values from <c>client.h</c>.</summary>
public enum MpvEventKind
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

/// <summary>mpv property value formats. Values from <c>client.h</c>.</summary>
public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

/// <summary>
/// One event marshalled out of mpv's own thread.
/// </summary>
/// <remarks>
/// Deliberately a plain record with no native pointers. mpv owns the memory an event
/// points at only until the next <c>mpv_wait_event</c> call on that handle, so every
/// value is copied out before the event is published. Handing a pointer to a consumer
/// would produce a use-after-free that depends on how fast the consumer runs.
/// </remarks>
public abstract record MpvEvent(MpvEventKind Kind);

/// <summary>An observed property changed.</summary>
public sealed record MpvPropertyChanged(string Name, MpvFormat Format, object? Value)
    : MpvEvent(MpvEventKind.PropertyChange)
{
    /// <summary>The value as a string, whatever format it arrived in.</summary>
    public string? AsString => Value?.ToString();

    /// <summary>The value as a flag, for <c>MPV_FORMAT_FLAG</c> properties.</summary>
    public bool AsFlag => Value is long value ? value != 0 : Value is true;

    /// <summary>The value as a number, for numeric properties.</summary>
    public double? AsNumber => Value switch
    {
        double d => d,
        long l => l,
        _ => null,
    };
}

/// <summary>A log line from mpv.</summary>
public sealed record MpvLogMessage(string Prefix, string Level, string Text)
    : MpvEvent(MpvEventKind.LogMessage);

/// <summary>Playback of a file ended.</summary>
/// <param name="Reason">
/// 0 end-of-file, 2 stop, 3 quit, 4 error, 5 redirect. Distinguishing these matters for
/// failover: an error is a candidate for switching provider, a clean EOF is not.
/// </param>
public sealed record MpvEndFile(int Reason, int Error) : MpvEvent(MpvEventKind.EndFile);

/// <summary>Any event carrying no payload this application reads.</summary>
public sealed record MpvSimpleEvent(MpvEventKind EventKind) : MpvEvent(EventKind);
