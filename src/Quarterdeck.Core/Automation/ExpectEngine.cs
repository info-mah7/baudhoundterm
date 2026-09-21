using System.Text;
using System.Text.RegularExpressions;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.Core.Automation;

/// <summary>
/// Runs an expect-style script against a live session: wait for a prompt, send a reply.
/// Used for auto-login (password, sudo, jump-host chains) and re-run after a reconnect.
/// </summary>
public sealed class ExpectEngine
{
    private readonly ITerminalSession _session;
    private readonly CredentialVault _vault;
    private readonly Guid _sessionId;
    private readonly StringBuilder _recent = new();
    private readonly object _gate = new();

    /// <summary>Only the tail is matched against; a long scrollback would make every step quadratic.</summary>
    private const int WindowSize = 8192;

    public ExpectEngine(ITerminalSession session, CredentialVault vault, Guid sessionId)
    {
        _session = session;
        _vault = vault;
        _sessionId = sessionId;
        _session.DataReceived += OnData;
    }

    private void OnData(object? sender, TerminalDataEventArgs e)
    {
        lock (_gate)
        {
            _recent.Append(e.Text);
            if (_recent.Length > WindowSize)
            {
                _recent.Remove(0, _recent.Length - WindowSize);
            }
        }
    }

    /// <summary>
    /// Runs every step in order. Returns the step that timed out, or null when all steps ran.
    /// A timeout is not an error by itself: a prompt that never appeared often just means
    /// the far end did not need it (an already-trusted key, say).
    /// </summary>
    public async Task<AutoLoginStep?> RunAsync(
        IReadOnlyList<AutoLoginStep> steps, CancellationToken cancellationToken = default)
    {
        foreach (var step in steps)
        {
            if (!string.IsNullOrEmpty(step.Expect))
            {
                var matched = await WaitForAsync(step, cancellationToken).ConfigureAwait(false);
                if (!matched)
                {
                    return step;
                }
            }

            var payload = step.IsSecret
                ? _vault.Get(_sessionId, step.SecretKey) ?? string.Empty
                : step.Send;

            if (step.AppendNewline)
            {
                payload += "\n";
            }

            await _session.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

            // Clear the window so the echo of what we just sent cannot satisfy the next step.
            lock (_gate)
            {
                _recent.Clear();
            }
        }

        return null;
    }

    private async Task<bool> WaitForAsync(AutoLoginStep step, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(step.TimeoutMs);
        var regex = step.IsRegex
            ? new Regex(step.Expect, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string window;
            lock (_gate)
            {
                window = _recent.ToString();
            }

            var hit = regex is not null
                ? regex.IsMatch(window)
                : window.Contains(step.Expect, StringComparison.OrdinalIgnoreCase);

            if (hit)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void Detach() => _session.DataReceived -= OnData;
}
