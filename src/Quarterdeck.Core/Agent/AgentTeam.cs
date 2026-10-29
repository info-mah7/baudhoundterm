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

    /// <summary>Gives the partner a message. Returns false once the partner's run has ended.</summary>
    public bool Send(string text)
    {
        Sent?.Invoke(text);
        if (Partner.Finished)
        {
            return false;
        }

        Partner.Receive(text);
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
            list.Add(m);
        }

        return list;
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a message, then returns what is waiting.</summary>
    public async Task<List<string>> WaitAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_inbox.IsEmpty && !Partner.Finished)
        {
            await _arrived.WaitAsync(timeout, token).ConfigureAwait(false);
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
}
