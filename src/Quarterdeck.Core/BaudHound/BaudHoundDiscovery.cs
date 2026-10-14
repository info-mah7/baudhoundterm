using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Quarterdeck.Core.BaudHound;

/// <summary>One BaudHound that answered.</summary>
/// <param name="Name">Its AP name, BaudHound-XXXX, unique per board.</param>
/// <param name="Address">Where this PC reaches it: the address the answer came from.</param>
/// <param name="Firmware">Firmware version, or "" when it did not say.</param>
/// <param name="Ssid">The network it has joined, or "" on its own AP only.</param>
/// <param name="LanAddress">Its address on that network, or "".</param>
/// <param name="TelnetPort">The port its UART is served on.</param>
/// <param name="Baud">Configured speed; 0 means auto-detect.</param>
/// <param name="Rate">The speed the UART is running at now, or 0 when unknown.</param>
/// <param name="Legacy">Found through its web page rather than discovery (firmware before 1.4).</param>
/// <param name="TlsPort">The TLS console's port, or 0 when it has none.</param>
/// <param name="TlsFingerprint">"SHA256:..." of its certificate, as its Settings page shows it.</param>
/// <param name="Locked">A TLS session is open on it: no other connection is accepted until it ends.</param>
public sealed record BaudHoundDevice(
    string Name,
    string Address,
    string Firmware,
    string Ssid,
    string LanAddress,
    int TelnetPort,
    int Baud,
    int Rate,
    bool Legacy,
    int TlsPort = 0,
    string TlsFingerprint = "",
    bool Locked = false);

/// <summary>
/// Finds BaudHounds on the networks this PC is on. Firmware 1.4 and later answer a UDP
/// broadcast of "BAUDHOUND?" on port 47023 with one JSON object. Older firmware has no
/// responder, so when this PC has joined a BaudHound's own access point (192.168.4.x), its
/// web status page at 192.168.4.1 is asked instead.
/// </summary>
public static class BaudHoundDiscovery
{
    public const int Port = 47023;
    private static readonly byte[] Query = Encoding.ASCII.GetBytes("BAUDHOUND?\n");

    public static async Task<IReadOnlyList<BaudHoundDevice>> FindAsync(
        TimeSpan window, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, BaudHoundDevice>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(window);

        var listen = ListenAsync(udp, found, stop.Token);

        // Three rounds: Wi-Fi drops broadcasts more often than unicast, and a device busy
        // scanning for a baud answers late.
        var targets = BroadcastTargets();
        for (var round = 0; round < 3 && !stop.IsCancellationRequested; round++)
        {
            foreach (var target in targets)
            {
                try
                {
                    await udp.SendAsync(Query, new IPEndPoint(target, Port), stop.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // An interface that cannot broadcast (VPN, down adapter): the others still can.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(round == 0 ? 250 : 500, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var legacy = OnBaudHoundAccessPoint()
            ? AskLegacyAsync(cancellationToken)
            : Task.FromResult<BaudHoundDevice?>(null);

        await listen.ConfigureAwait(false);

        if (await legacy.ConfigureAwait(false) is { } old && !found.ContainsKey(old.Name))
        {
            found[old.Name] = old;
        }

        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task ListenAsync(UdpClient udp, Dictionary<string, BaudHoundDevice> found, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try
            {
                reply = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Windows reports an ICMP port-unreachable from an earlier send here. Keep listening.
                continue;
            }

            if (Parse(reply.Buffer, reply.RemoteEndPoint.Address) is { } device)
            {
                found[device.Name] = device;
            }
        }
    }

    /// <summary>Reads one discovery answer. Anything that is not one is ignored.</summary>
    public static BaudHoundDevice? Parse(byte[] payload, IPAddress from)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("baudhound", out var marker)
                || marker.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var name = Text(root, "name");
            if (name.Length == 0)
            {
                return null;
            }

            var lan = Text(root, "lanip");
            return new BaudHoundDevice(
                name,
                from.ToString(),
                Text(root, "fw"),
                Text(root, "ssid"),
                lan,
                Number(root, "telnet", 23),
                Number(root, "baud", 0),
                Number(root, "rate", 0),
                Legacy: false,
                Number(root, "tls", 0),
                Text(root, "tlsfp"),
                Flag(root, "locked"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Firmware before 1.4: the only one reachable without knowing its address is the one whose
    /// access point this PC has joined, always at 192.168.4.1.
    /// </summary>
    private static async Task<BaudHoundDevice?> AskLegacyAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = await http.GetStringAsync("http://192.168.4.1/status", token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            var name = Text(root, "ap");
            if (!name.StartsWith("BaudHound", StringComparison.Ordinal))
            {
                return null;
            }

            var joined = root.TryGetProperty("sta", out var sta) && sta.ValueKind == JsonValueKind.True;
            var lan = Text(root, "ip");
            return new BaudHoundDevice(
                name,
                "192.168.4.1",
                Text(root, "fw"),
                joined ? Text(root, "ssid") : "",
                joined && lan != "-" ? lan : "",
                23,
                Number(root, "baud", 0),
                Number(root, "rate", 0),
                Legacy: true,
                Number(root, "tls", 0),
                Text(root, "tlsfp"),
                Flag(root, "locked"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The limited broadcast, plus each interface's directed broadcast: Windows sends
    /// 255.255.255.255 out of one interface only, so a PC on both a LAN and a BaudHound's AP
    /// would otherwise find just one of them.
    /// </summary>
    private static List<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };

        foreach (var (address, mask) in LocalIPv4())
        {
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                b[i] = (byte)(a[i] | ~m[i]);
            }

            var broadcast = new IPAddress(b);
            if (!targets.Contains(broadcast))
            {
                targets.Add(broadcast);
            }
        }

        return targets;
    }

    private static bool OnBaudHoundAccessPoint() =>
        LocalIPv4().Any(x => x.Address.GetAddressBytes() is [192, 168, 4, not 1]);

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                // A /32 (point-to-point VPN) has no broadcast address worth sending to.
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && unicast.IPv4Mask is { } mask
                    && !mask.Equals(IPAddress.Broadcast)
                    && !mask.Equals(IPAddress.Any))
                {
                    yield return (unicast.Address, mask);
                }
            }
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
}
