using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using Microsoft.Win32;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// An RDP tab: Windows' own Remote Desktop control (mstscax.dll, the engine inside mstsc),
/// hosted through Windows Forms. It is driven late-bound through IDispatch, so no interop
/// assembly has to be generated or shipped, and the newest control the PC has is used.
/// State comes from polling the control's Connected property; that avoids an event sink
/// for its COM events, which differ between control versions.
/// </summary>
internal sealed class RdpView : Grid, IDisposable
{
    // MsRdpClient9..12 "not safe for scripting": the variants that let the host set the
    // password. Newest first; Windows 10 and 11 register all of them.
    private static readonly string[] ProgIds =
    [
        "MsTscAx.MsTscAx.13", "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10"
    ];

    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private readonly Action<SessionState, string?> _report;
    private readonly Func<string, string, string?> _promptForSecret;
    private readonly WindowsFormsHost _host = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private RdpAxHost? _ax;
    private int _lastState = -1;
    private bool _userDisconnect;
    private (int W, int H) _sentSize;

    public RdpView(SessionDefinition definition, CredentialVault vault,
                   Action<SessionState, string?> report, Func<string, string, string?> promptForSecret)
    {
        _definition = definition;
        _vault = vault;
        _report = report;
        _promptForSecret = promptForSecret;
        Background = System.Windows.Media.Brushes.Black;
        Children.Add(_host);

        _poll.Tick += (_, _) => Poll();
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            FitToTab();
        };
        SizeChanged += (_, _) =>
        {
            _resize.Stop();
            _resize.Start();
        };
    }

    /// <summary>The control's dispatch object; dynamic because no interop assembly is shipped.</summary>
    private dynamic? Rdp => _ax?.Ocx;

    private static string? FindClsid()
    {
        foreach (var progId in ProgIds)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(progId + @"\CLSID");
            if (key?.GetValue("") is string clsid && clsid.Length > 0)
            {
                return clsid;
            }
        }

        return null;
    }

    private bool EnsureControl()
    {
        if (_ax is not null)
        {
            return true;
        }

        var clsid = FindClsid();
        if (clsid is null)
        {
            _report(SessionState.Failed, "This PC has no Remote Desktop control (mstscax.dll, Windows 10 or later).");
            return false;
        }

        _ax = new RdpAxHost(clsid) { Dock = System.Windows.Forms.DockStyle.Fill };
        _host.Child = _ax;
        _ax.CreateControl();
        return true;
    }

    public async Task ConnectAsync()
    {
        _userDisconnect = false;
        if (string.IsNullOrWhiteSpace(_definition.Host))
        {
            _report(SessionState.Failed, "No host set. Add one in Properties.");
            return;
        }

        if (!EnsureControl())
        {
            return;
        }

        // A reconnect reuses the control; it must be idle before new settings go in.
        if (Rdp is { } busy && (int)busy.Connected != 0)
        {
            busy.Disconnect();
            for (var i = 0; i < 20 && (int)busy.Connected != 0; i++)
            {
                await Task.Delay(100);
            }
        }

        _report(SessionState.Connecting, $"Connecting to {_definition.Host}...");

        string username;
        string? password;
        try
        {
            (username, password) = await CredentialsAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or KeePassXcException)
        {
            _report(SessionState.Failed, ex.Message);
            return;
        }

        try
        {
            var rdp = Rdp!;
            rdp.Server = _definition.Host.Trim();
            rdp.UserName = username;
            if (_definition.RdpDomain.Length > 0)
            {
                rdp.Domain = _definition.RdpDomain;
            }

            var settings = rdp.AdvancedSettings9;
            settings.RDPPort = _definition.Port > 0 ? _definition.Port : 3389;
            settings.EnableCredSspSupport = true;        // NLA, which most servers require
            settings.AuthenticationLevel = 2;            // the control asks when it cannot verify the server
            settings.RedirectClipboard = _definition.RdpClipboard;
            settings.RedirectDrives = _definition.RdpDrives;
            settings.ConnectToAdministerServer = _definition.RdpAdmin;
            settings.SmartSizing = false;
            if (!string.IsNullOrEmpty(password))
            {
                settings.ClearTextPassword = password;
            }

            // Windows key, Alt+Tab and the like go to the remote desktop while it has focus.
            rdp.SecuredSettings3.KeyboardHookMode = 1;

            if (_definition.RdpGateway.Length > 0)
            {
                var transport = rdp.TransportSettings2;
                transport.GatewayHostname = _definition.RdpGateway;
                transport.GatewayUsageMethod = 1;        // always use the gateway
                transport.GatewayProfileUsageMethod = 1; // explicit settings, not the user's defaults
                transport.GatewayCredSharing = 1;        // same login for the gateway and the server
            }

            var (w, h) = TabPixels();
            rdp.ColorDepth = 32;
            rdp.DesktopWidth = w;
            rdp.DesktopHeight = h;
            _sentSize = (w, h);

            _lastState = -1;
            rdp.Connect();
            _poll.Start();
        }
        catch (Exception ex)
        {
            _report(SessionState.Failed, "The Remote Desktop control refused the settings: " + ex.Message);
        }
    }

    /// <summary>
    /// User name and password from the shortcut, KeePassXC, or a prompt. Not saving the
    /// password is the recommended setup, so asking is the normal path, not an error.
    /// </summary>
    private async Task<(string User, string? Password)> CredentialsAsync()
    {
        var username = _definition.Username.Trim();
        string? password = null;

        if (_definition.PasswordSource == PasswordSource.KeePassXc)
        {
            // Blank entry: look for an entry with the URL rdp://host (and :port when not 3389).
            var entry = _definition.KeePassEntry.Trim();
            if (entry.Length == 0)
            {
                entry = "rdp://" + _definition.Host.Trim() + (_definition.Port is 3389 or 0 ? "" : $":{_definition.Port}");
            }

            var login = await KeePassXcPasswords.LookupAsync(
                _vault, entry, _definition.Host, _definition.Port, username);
            password = login.Password;
            if (username.Length == 0)
            {
                username = login.Login;
            }
        }
        else
        {
            password = _vault.Get(_definition.Id);
        }

        if (username.Length == 0)
        {
            var dialog = new CredentialPromptWindow($"{_definition.Host}:{_definition.Port}", Environment.UserName)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true)
            {
                throw new OperationCanceledException("Cancelled.");
            }

            username = dialog.Username;
            if (dialog.Password.Length > 0)
            {
                password = dialog.Password;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            password = _promptForSecret($"Password for {username}", "password");
            if (password is null)
            {
                throw new OperationCanceledException("Cancelled.");
            }
        }

        return (username, password);
    }

    /// <summary>The tab's size in device pixels, as RDP wants it: even, and within its limits.</summary>
    private (int W, int H) TabPixels()
    {
        var w = _ax is { Width: > 0 } ? _ax.Width : 1280;
        var h = _ax is { Height: > 0 } ? _ax.Height : 800;
        return (Math.Clamp(w & ~1, 200, 8192), Math.Clamp(h & ~1, 200, 8192));
    }

    /// <summary>A resized tab resizes the remote desktop (Windows 8.1 / Server 2012 R2 and later).</summary>
    private void FitToTab()
    {
        if (Rdp is not { } rdp || _lastState != 1)
        {
            return;
        }

        var (w, h) = TabPixels();
        if ((w, h) == _sentSize)
        {
            return;
        }

        try
        {
            rdp.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, 100u, 100u);
            _sentSize = (w, h);
        }
        catch (Exception)
        {
            // Older servers cannot resize a live session; the picture stays at its first size.
        }
    }

    private void Poll()
    {
        if (Rdp is not { } rdp)
        {
            return;
        }

        int state;
        try
        {
            state = (int)rdp.Connected;   // 0 disconnected, 1 connected, 2 connecting
        }
        catch (Exception)
        {
            return;
        }

        if (state == _lastState)
        {
            return;
        }

        var was = _lastState;
        _lastState = state;

        switch (state)
        {
            case 2:
                _report(SessionState.Connecting, $"Connecting to {_definition.Host}...");
                break;
            case 1:
                _report(SessionState.Connected, $"Connected to {_definition.Host}.");
                FitToTab();
                break;
            default:
                _poll.Stop();
                if (_userDisconnect)
                {
                    _report(SessionState.Disconnected, "Disconnected.");
                }
                else
                {
                    var reason = Reason(rdp);
                    _report(was == 1 ? SessionState.Disconnected : SessionState.Failed,
                            (was == 1 ? "The session ended" : "Could not connect") + (reason.Length > 0 ? ": " + reason : ".") +
                            " Press Reconnect to try again.");
                }

                break;
        }
    }

    /// <summary>The control's extended reason, in words where it is a common one.</summary>
    private static string Reason(dynamic rdp)
    {
        int code;
        try
        {
            code = (int)rdp.ExtendedDisconnectReason;
        }
        catch (Exception)
        {
            return "";
        }

        return code switch
        {
            0 => "",
            1 => "an administrator ended the session",
            2 => "an administrator logged the session off",
            3 => "the session was idle too long",
            4 => "the session reached its time limit",
            5 => "another user connected to the session",
            6 => "the server ran out of memory",
            7 => "the server refused the connection",
            9 => "this user is not allowed to log on remotely",
            12 => "the user logged off",
            _ => $"reason {code}"
        };
    }

    public Task DisconnectAsync()
    {
        _userDisconnect = true;
        try
        {
            if (Rdp is { } rdp && (int)rdp.Connected != 0)
            {
                rdp.Disconnect();
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _poll.Stop();
        _resize.Stop();
        try
        {
            if (Rdp is { } rdp && (int)rdp.Connected != 0)
            {
                rdp.Disconnect();
            }
        }
        catch (Exception)
        {
        }

        _host.Child = null;
        _ax?.Dispose();
        _ax = null;
        _host.Dispose();
    }
}

/// <summary>Hosts an ActiveX control by CLSID and hands out its dispatch object.</summary>
internal sealed class RdpAxHost(string clsid) : System.Windows.Forms.AxHost(clsid.Trim('{', '}'))
{
    public object? Ocx => GetOcx();
}
