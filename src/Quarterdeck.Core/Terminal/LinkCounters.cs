namespace Quarterdeck.Core.Terminal;

/// <summary>
/// Bytes moved on a link, in each direction. Transports count as they read and write; the UI
/// polls the totals to light its TX and RX lamps, so a fast stream costs one timer tick rather
/// than one dispatch per chunk. Lives on the controller, so it survives a reconnect.
/// </summary>
public sealed class LinkCounters
{
    private long _sent;
    private long _received;

    public long Sent => Interlocked.Read(ref _sent);
    public long Received => Interlocked.Read(ref _received);

    public void AddSent(long count) => Interlocked.Add(ref _sent, count);
    public void AddReceived(long count) => Interlocked.Add(ref _received, count);
}
