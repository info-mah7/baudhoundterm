using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.BaudHound;

/// <summary>What a BaudHound connection looks like as a session.</summary>
public static class BaudHoundSessions
{
    public const string Folder = "BaudHound";

    /// <summary>
    /// Telnet in line mode, because the device reads whole lines and echoes nothing, with its
    /// ~ commands on the right-click menu. Reconnect stays on: the device drops telnet clients
    /// whenever it reboots or rejoins Wi-Fi.
    /// </summary>
    public static SessionDefinition Create(string name, string host, int port = 23) => new()
    {
        Name = name,
        Folder = Folder,
        Protocol = SessionProtocol.Telnet,
        Host = host,
        Port = port,
        LineMode = true,
        TabColor = "#e5c07b",
        Commands = CommandShortcut.BaudHound(),
        Tags = new List<string> { "baudhound" }
    };
}
