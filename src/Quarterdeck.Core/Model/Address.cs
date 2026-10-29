namespace Quarterdeck.Core.Model;

/// <summary>Addresses as typed into Quick connect.</summary>
public static class Address
{
    /// <summary>
    /// Takes a trailing ":port" off an address and returns it. "[2001:db8::10]:22" and
    /// "host:22" carry one; a bare IPv6 literal ("2001:db8::10", "::1") is left whole, however
    /// its last group reads as a number.
    /// </summary>
    public static int? SplitPort(ref string address)
    {
        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out var port))
        {
            return null;
        }

        var host = text[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            address = host[1..^1];
            return port;
        }

        if (host.Contains(':'))
        {
            return null;   // a bare IPv6 literal
        }

        address = host;
        return port;
    }
}
