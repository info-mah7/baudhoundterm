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

    private readonly Func<string, string, string?>? _prompt;

    /// <param name="prompt">Asks the user for a secret that is not saved: (question, vault key).</param>
    public ExpectEngine(ITerminalSession session, CredentialVault vault, Guid sessionId,
                        Func<string, string, string?>? prompt = null)
    {
        _session = session;
        _vault = vault;
        _sessionId = sessionId;
        _prompt = prompt;
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
    /// Runs every step in order. Returns null when all steps ran, or the step that stopped the
    /// script and why. A timeout is not an error by itself: a prompt that never appeared often
    /// just means the far end did not need it (an already-trusted key, say). Problems with the
    /// script itself (a bad pattern, a secret that is not saved, a failed send) also stop it
    /// here rather than escaping, so they never cost the user a working connection.
    /// </summary>
    public async Task<ExpectStop?> RunAsync(
        IReadOnlyList<AutoLoginStep> steps, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            try
            {
                if (!string.IsNullOrEmpty(step.Expect))
                {
                    var matched = await WaitForAsync(step, cancellationToken).ConfigureAwait(false);
                    if (!matched)
                    {
                        return new ExpectStop(step, i + 1, $"never saw \"{step.Expect}\"");
                    }
                }

                string payload;
                if (step.IsSecret)
                {
                    // Sending an empty line at a password prompt is a failed login that counts
                    // toward lockout; stop and say which secret is missing instead.
                    // Not saved (the safer choice): ask, as an SSH login would.
                    var secret = _vault.Get(_sessionId, step.SecretKey)
                                 ?? _prompt?.Invoke($"Auto-login step {i + 1} wants the {step.SecretKey}", step.SecretKey);
                    if (string.IsNullOrEmpty(secret))
                    {
                        return new ExpectStop(step, i + 1, $"no {step.SecretKey} saved or given for this shortcut");
                    }

                    payload = secret;
                }
                else
                {
                    payload = step.Send;
                }

                if (step.AppendNewline)
                {
                    payload += "\n";
                }

                await _session.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or IOException
                                           or InvalidOperationException or ObjectDisposedException)
            {
                return new ExpectStop(step, i + 1, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the step's prompt, then consumes the window up to the end of the match, so
    /// the same prompt cannot satisfy the next step while anything after it is kept: a server
    /// that prints the next prompt right away must not have it thrown out.
    /// </summary>
    private async Task<bool> WaitForAsync(AutoLoginStep step, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(step.TimeoutMs);
        var regex = step.IsRegex
            ? new Regex(step.Expect, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            : null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var window = _recent.ToString();
                int end;
                if (regex is not null)
                {
                    var m = regex.Match(window);
                    end = m.Success ? m.Index + m.Length : -1;
                }
                else
                {
                    var at = window.IndexOf(step.Expect, StringComparison.OrdinalIgnoreCase);
                    end = at < 0 ? -1 : at + step.Expect.Length;
                }

                if (end >= 0)
                {
                    _recent.Remove(0, end);
                    return true;
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void Detach() => _session.DataReceived -= OnData;
}

/// <summary>Where a login script stopped: the step, its 1-based number, and why.</summary>
public sealed record ExpectStop(AutoLoginStep Step, int Number, string Reason);
