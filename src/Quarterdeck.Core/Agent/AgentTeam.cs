using System.Collections.Concurrent;

namespace Quarterdeck.Core.Agent;

/// <summary>
/// Two agents on two devices (a FortiGate at each end of a tunnel) working on one goal. Each
/// side holds a member: what it sends lands in the partner's inbox, and the runner hands the
/// inbox to its model before every step and through wait_for_partner. The channel lives in
/// this process only; nothing of it is written anywhere.
/// </summary>
public static class AgentTeam
{
    /// <summary>Two linked members, one for each pane.</summary>
    public static (AgentTeamMember A, AgentTeamMember B) Pair(string deviceA, string deviceB)
    {
        var a = new AgentTeamMember(deviceA);
        var b = new AgentTeamMember(deviceB);
        a.Partner = b;
        b.Partner = a;
        return (a, b);
    }
}

public sealed class AgentTeamMember
{
    private readonly ConcurrentQueue<string> _inbox = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private int _wakes;

    /// <summary>How many times a finished side is started again by a late message.</summary>
    public const int MaxWakes = 2;

    internal AgentTeamMember(string device)
    {
        Device = device;
    }

    /// <summary>How this side's device is described to both agents.</summary>
    public string Device { get; }

    public AgentTeamMember Partner { get; internal set; } = null!;

    /// <summary>Set once this side's run ended; the partner is told, with the answer.</summary>
    public bool Finished { get; private set; }

    public bool PartnerFinished => Partner.Finished;

    /// <summary>Messages the partner sent and this side has not read yet.</summary>
    public bool HasMail => !_inbox.IsEmpty;

    /// <summary>Raised on the sender's side with each message, for the pane to show.</summary>
    public event Action<string>? Sent;

    /// <summary>Raised on the receiver's side, for the pane to show.</summary>
    public event Action<string>? Received;

    /// <summary>
    /// Set by the pane once this side has finished: called when the partner writes anyway, it
    /// starts the run again to read the message and returns true, or returns false (the user
    /// stopped the run, or the pane is busy with something else).
    /// </summary>
    public Func<bool>? WakeUp { get; set; }

    /// <summary>
    /// Gives the partner a message. A partner that has finished is started again to read it, up
    /// to <see cref="MaxWakes"/> times; returns false when it cannot be.
    /// </summary>
    public bool Send(string text)
    {
        Sent?.Invoke(text);
        if (Partner.Finished && !Partner.Wake())
        {
            return false;
        }

        Partner.Receive(text);
        return true;
    }

    // Only a partner's message wakes a side, never the partner's own finished notice: two sides
    // finishing one after the other would otherwise wake each other for ever.
    private bool Wake()
    {
        if (_wakes >= MaxWakes || WakeUp is not { } wake)
        {
            return false;
        }

        Finished = false;   // before the message lands, so the restarted run reads it
        if (!wake())
        {
            Finished = true;
            return false;
        }

        _wakes++;
        WakeUp = null;
        return true;
    }

    private void Receive(string text)
    {
        _inbox.Enqueue(text);
        _arrived.Release();
        Received?.Invoke(text);
    }

    /// <summary>Everything waiting, oldest first; empty when nothing is.</summary>
    public List<string> Take()
    {
        var list = new List<string>();
        while (_inbox.TryDequeue(out var m))
        {
            // Each message released one permit when it arrived: take it back with the message,
            // or a later wait_for_partner finds a stale permit and returns at once with nothing.
            _arrived.Wait(0);
            list.Add(m);
        }

        return list;
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a message, then returns what is waiting.</summary>
    public async Task<List<string>> WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        // A permit can still outrun its message (Receive enqueues, then releases, while Take
        // drains between the two), so wake only for a message that is really there.
        var deadline = DateTime.UtcNow + timeout;
        while (_inbox.IsEmpty && !Partner.Finished)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left, token).ConfigureAwait(false))
            {
                break;
            }
        }

        return Take();
    }

    /// <summary>This side's run ended: the partner learns the answer and stops waiting.</summary>
    public void Finish(string answer)
    {
        if (Finished)
        {
            return;
        }

        Finished = true;
        Partner.Receive($"[The agent on {Device} has finished. Its final answer:]\n{answer}");
    }

    /// <summary>This side never started (no endpoint, say): the partner is told at once, not left waiting.</summary>
    public void Abandon(string reason)
    {
        if (Finished)
        {
            return;
        }

        Finished = true;
        Partner.Receive($"[The agent on {Device} could not start ({reason}), so it is not taking part. Do not wait for it: " +
                        "do your own side, and say in your answer what the other side still needs.]");
    }
}
