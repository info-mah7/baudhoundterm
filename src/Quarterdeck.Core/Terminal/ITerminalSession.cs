namespace Quarterdeck.Core.Terminal;

public sealed class TerminalDataEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public sealed class TerminalClosedEventArgs(string? reason, bool wasExpected) : EventArgs
{
    public string? Reason { get; } = reason;

    /// <summary>False when the far end dropped unexpectedly, which is what arms auto-reconnect.</summary>
    public bool WasExpected { get; } = wasExpected;
}

/// <summary>
/// A live byte stream to somewhere: an SSH channel, a serial port, or a local ConPTY.
/// Everything above this interface (logging, auto-login, reconnect) is transport agnostic.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<TerminalDataEventArgs>? DataReceived;
    event EventHandler<TerminalClosedEventArgs>? Closed;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Send keystrokes or script text to the far end.</summary>
    Task WriteAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Tell the far end the window changed size. Serial ports ignore this.</summary>
    Task ResizeAsync(int columns, int rows);

    Task DisconnectAsync();
}
