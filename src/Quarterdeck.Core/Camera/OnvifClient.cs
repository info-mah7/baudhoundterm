using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Quarterdeck.Core.Camera;

/// <summary>A camera's ONVIF services as discovered at connect.</summary>
public sealed record OnvifCamera(string MediaUrl, string? PtzUrl, string ProfileToken, bool HasPtz);

public sealed record OnvifPreset(string Token, string Name)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Token : Name;
}

/// <summary>
/// Just enough ONVIF for a viewer: find the RTSP stream, and drive pan, tilt, zoom and presets.
/// Authenticates both ways cameras expect: a WS-Security UsernameToken (password digest) in
/// every request, and HTTP Basic/Digest when the camera challenges at the HTTP layer.
/// </summary>
public sealed class OnvifClient : IDisposable
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Trt = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";

    private readonly HttpClient _http;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;

    public OnvifClient(string host, int port, string user, string password)
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
        _http = new HttpClient(new HttpClientHandler
        {
            Credentials = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, password),
            PreAuthenticate = false
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public string DeviceUrl => $"http://{_host}:{_port}/onvif/device_service";

    /// <summary>Finds the media and PTZ services and the first media profile.</summary>
    public async Task<OnvifCamera> DiscoverAsync(CancellationToken token = default)
    {
        var caps = await CallAsync(DeviceUrl,
            new XElement(Tds + "GetCapabilities", new XElement(Tds + "Category", "All")), token);

        var media = caps.Descendants(Tt + "Media").Elements(Tt + "XAddr").FirstOrDefault()?.Value
                    ?? throw new IOException("The camera answered ONVIF but listed no media service.");
        var ptz = caps.Descendants(Tt + "PTZ").Elements(Tt + "XAddr").FirstOrDefault()?.Value;

        media = Rehost(media);
        ptz = ptz is null ? null : Rehost(ptz);

        var profiles = await CallAsync(media, new XElement(Trt + "GetProfiles"), token);
        var first = profiles.Descendants(Trt + "Profiles").FirstOrDefault()
                    ?? throw new IOException("The camera has no media profiles.");
        var profileToken = (string?)first.Attribute("token") ?? throw new IOException("Profile without a token.");
        var hasPtz = ptz is not null && first.Element(Tt + "PTZConfiguration") is not null;

        return new OnvifCamera(media, ptz, profileToken, hasPtz);
    }

    /// <summary>
    /// Cameras behind NAT or with a wrong network setting report service addresses with an
    /// unreachable host; keep their path and port but use the address we actually reached.
    /// </summary>
    private string Rehost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return url;
        }

        var builder = new UriBuilder(u) { Host = _host };
        if (u.IsDefaultPort)
        {
            builder.Port = _port;
        }

        return builder.Uri.ToString();
    }

    public async Task<string> GetStreamUriAsync(OnvifCamera camera, CancellationToken token = default)
    {
        var body = new XElement(Trt + "GetStreamUri",
            new XElement(Trt + "StreamSetup",
                new XElement(Tt + "Stream", "RTP-Unicast"),
                new XElement(Tt + "Transport", new XElement(Tt + "Protocol", "RTSP"))),
            new XElement(Trt + "ProfileToken", camera.ProfileToken));

        var reply = await CallAsync(camera.MediaUrl, body, token);
        var uri = reply.Descendants(Tt + "Uri").FirstOrDefault()?.Value
                  ?? throw new IOException("The camera returned no stream address.");

        // The stream address often names the camera's own idea of its IP; use the one we reached.
        return Uri.TryCreate(uri, UriKind.Absolute, out var u)
            ? new UriBuilder(u) { Host = _host }.Uri.ToString()
            : uri;
    }

    /// <summary>Starts moving: each axis -1..1 (left/down/out to right/up/in). Stop with <see cref="StopAsync"/>.</summary>
    public Task ContinuousMoveAsync(OnvifCamera camera, double pan, double tilt, double zoom, CancellationToken token = default)
    {
        var velocity = new XElement(Tptz + "Velocity");
        if (pan != 0 || tilt != 0)
        {
            velocity.Add(new XElement(Tt + "PanTilt", new XAttribute("x", F(pan)), new XAttribute("y", F(tilt))));
        }

        if (zoom != 0)
        {
            velocity.Add(new XElement(Tt + "Zoom", new XAttribute("x", F(zoom))));
        }

        return CallAsync(RequirePtz(camera), new XElement(Tptz + "ContinuousMove",
            new XElement(Tptz + "ProfileToken", camera.ProfileToken), velocity), token);
    }

    public Task StopAsync(OnvifCamera camera, CancellationToken token = default) =>
        CallAsync(RequirePtz(camera), new XElement(Tptz + "Stop",
            new XElement(Tptz + "ProfileToken", camera.ProfileToken),
            new XElement(Tptz + "PanTilt", "true"),
            new XElement(Tptz + "Zoom", "true")), token);

    public async Task<List<OnvifPreset>> GetPresetsAsync(OnvifCamera camera, CancellationToken token = default)
    {
        var reply = await CallAsync(RequirePtz(camera), new XElement(Tptz + "GetPresets",
            new XElement(Tptz + "ProfileToken", camera.ProfileToken)), token);

        return reply.Descendants(Tptz + "Preset")
            .Select(p => new OnvifPreset((string?)p.Attribute("token") ?? "", p.Element(Tt + "Name")?.Value ?? ""))
            .Where(p => p.Token.Length > 0)
            .ToList();
    }

    public Task GotoPresetAsync(OnvifCamera camera, string presetToken, CancellationToken token = default) =>
        CallAsync(RequirePtz(camera), new XElement(Tptz + "GotoPreset",
            new XElement(Tptz + "ProfileToken", camera.ProfileToken),
            new XElement(Tptz + "PresetToken", presetToken)), token);

    private static string RequirePtz(OnvifCamera camera) =>
        camera.PtzUrl ?? throw new NotSupportedException("This camera has no PTZ service.");

    private static string F(double v) => Math.Clamp(v, -1, 1).ToString("0.###", CultureInfo.InvariantCulture);

    private async Task<XElement> CallAsync(string url, XElement body, CancellationToken token)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XElement(Soap + "Header", SecurityHeader()),
            new XElement(Soap + "Body", body));

        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };

        using var response = await _http.PostAsync(url, content, token);
        var text = await response.Content.ReadAsStringAsync(token);

        XElement reply;
        try
        {
            reply = XElement.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new IOException($"{url} answered {(int)response.StatusCode} without ONVIF XML.");
        }

        var fault = reply.Descendants(Soap + "Fault").FirstOrDefault();
        if (fault is not null)
        {
            var reason = fault.Descendants(Soap + "Text").FirstOrDefault()?.Value ?? "fault";
            throw response.StatusCode == HttpStatusCode.Unauthorized || reason.Contains("auth", StringComparison.OrdinalIgnoreCase)
                ? new UnauthorizedAccessException($"The camera refused the login: {reason}")
                : new IOException($"ONVIF fault: {reason}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode == HttpStatusCode.Unauthorized
                ? new UnauthorizedAccessException("The camera refused the login.")
                : new IOException($"{url} answered {(int)response.StatusCode}.");
        }

        return reply.Element(Soap + "Body") ?? reply;
    }

    /// <summary>WS-Security UsernameToken with a password digest, as ONVIF Core specifies.</summary>
    private XElement? SecurityHeader()
    {
        if (string.IsNullOrEmpty(_user))
        {
            return null;
        }

        XNamespace wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var digest = Convert.ToBase64String(SHA1.HashData(
            nonce.Concat(Encoding.UTF8.GetBytes(created)).Concat(Encoding.UTF8.GetBytes(_password)).ToArray()));

        return new XElement(wsse + "Security",
            new XAttribute(Soap + "mustUnderstand", "1"),
            new XElement(wsse + "UsernameToken",
                new XElement(wsse + "Username", _user),
                new XElement(wsse + "Password",
                    new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"),
                    digest),
                new XElement(wsse + "Nonce",
                    new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"),
                    Convert.ToBase64String(nonce)),
                new XElement(wsu + "Created", created)));
    }

    public void Dispose() => _http.Dispose();
}
