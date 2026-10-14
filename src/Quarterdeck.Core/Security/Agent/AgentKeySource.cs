using Renci.SshNet;
using Renci.SshNet.Security;

namespace Quarterdeck.Core.Security.Agent;

/// <summary>
/// Agent keys as SSH.NET key sources. SSH.NET offers each key to the server first and only
/// asks for a signature on the one it accepts, so the agent signs once per login.
/// </summary>
public sealed class AgentKeySource : IPrivateKeySource
{
    private AgentKeySource(IReadOnlyCollection<HostAlgorithm> algorithms) => HostKeyAlgorithms = algorithms;

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; }

    /// <summary>
    /// Keys from every agent on this machine, optionally narrowed by <paramref name="filter"/>:
    /// part of the key's comment, or its SHA256 fingerprint. Narrowing matters with many keys:
    /// servers drop the connection after a handful of rejected ones (MaxAuthTries).
    /// </summary>
    public static AgentKeySource FromAgents(string? filter)
    {
        var keys = SshAgentClient.ListIdentities(out var problems);

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(problems.Count > 0
                ? "No SSH agent answered: " + string.Join("; ", problems)
                : "No SSH agent has keys. Start the OpenSSH agent or Pageant, and unlock KeePassXC so it can add its keys.");
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            var narrowed = keys.Where(k =>
                k.Fingerprint.Equals(f, StringComparison.Ordinal) ||
                k.Fingerprint.Equals("SHA256:" + f, StringComparison.Ordinal) ||
                k.Comment.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();

            keys = narrowed.Count > 0
                ? narrowed
                : throw new InvalidOperationException(
                    $"None of the agent's {keys.Count} key(s) matches \"{f}\". Offered: {string.Join(", ", keys.Select(k => k.Comment))}.");
        }

        var algorithms = new List<HostAlgorithm>();
        foreach (var key in keys)
        {
            algorithms.AddRange(AlgorithmsFor(key));
        }

        return new AgentKeySource(algorithms);
    }

    /// <summary>
    /// An RSA key can sign with SHA-512, SHA-256 or SHA-1. Servers since OpenSSH 8.8 refuse the
    /// last, and older ones only know it, so all three are offered, strongest first.
    /// </summary>
    private static IEnumerable<HostAlgorithm> AlgorithmsFor(AgentIdentity key) => key.KeyType switch
    {
        "ssh-rsa" => new[]
        {
            new AgentHostAlgorithm("rsa-sha2-512", key, SshAgentClient.RsaSha512),
            new AgentHostAlgorithm("rsa-sha2-256", key, SshAgentClient.RsaSha256),
            new AgentHostAlgorithm("ssh-rsa", key, 0)
        },
        "ssh-rsa-cert-v01@openssh.com" => new[]
        {
            new AgentHostAlgorithm("rsa-sha2-512-cert-v01@openssh.com", key, SshAgentClient.RsaSha512),
            new AgentHostAlgorithm("rsa-sha2-256-cert-v01@openssh.com", key, SshAgentClient.RsaSha256),
            new AgentHostAlgorithm("ssh-rsa-cert-v01@openssh.com", key, 0)
        },
        _ => new[] { new AgentHostAlgorithm(key.KeyType, key, 0) }
    };

    private sealed class AgentHostAlgorithm(string name, AgentIdentity key, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => key.Blob;

        public override byte[] Sign(byte[] data) => SshAgentClient.Sign(key, data, flags);

        // Only used to check a server's host key, never for a client key.
        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }
}
