# BaudHoundTerm

Terminal and session manager for Windows 11, in the shape of SecureCRT: saved sessions in
folders, an encrypted credential store, session logging, scripted logins, auto-reconnect, and
SFTP/SCP transfer. SSH, telnet, serial and local shells. One self-contained executable, no
runtime to install.

It is also the desktop console for [BaudHound](https://github.com/magicsmokepress/BaudHound),
the pocket ESP32-S3 wireless serial tap: BaudHound puts a UART on the network over telnet, and
BaudHoundTerm gives that connection saved sessions, logging, highlight rules and reconnect.

Apache-2.0.

## Why it does not wrap Windows Terminal

The obvious design is to shell out to `wt.exe` with a generated profile. It cannot work for this
feature set: Windows Terminal owns the pseudoconsole and exposes no way to read the output stream
or inject into the input stream. Everything interesting here (logging a transcript, colouring
matching lines, waiting for a password prompt and answering it, searching the scrollback) needs
the stream. So Quarterdeck hosts the terminal itself: a ConPTY for local shells, SSH.NET channels
for SSH, and a serial port for consoles, rendered by xterm.js.

## Features

- **Sessions** in a folder tree, filterable by name, host or tag; clone any session with its
  stored credentials.
- **Credential vault** encrypted with DPAPI under the current user. Secrets never enter
  `sessions.json`; the file is useless on another machine or account.
- **Protocols**: SSH (password, public key, agent, keyboard-interactive), serial, local shell.
- **Session logging** to file, optionally timestamped per line and stripped of ANSI sequences,
  with size-based rolling.
- **Auto-login scripting**: expect/send steps with literal or regex matching and a timeout per
  step, re-run after a reconnect. The password comes from the vault, never from the script text.
- **Auto-reconnect** with exponential backoff and an attempt cap.
- **File transfer**: SFTP browser with upload and download, or SCP for hosts with no SFTP
  subsystem. Remote paths are shell-quoted.
- **Terminal**: 24-bit colour, three themes, configurable font and scrollback, find in scrollback,
  clickable links, Ctrl+Shift+C/V and right-click copy-paste.

## Build

Requires the .NET 10 SDK. Builds on Windows or, with `EnableWindowsTargeting`, on Linux.

```
dotnet publish src/Quarterdeck.App/Quarterdeck.App.csproj -c Release
```

The result is `BaudHoundTerm.exe` (about 64 MB, self-contained) plus an `Assets` folder that must
travel with it.

## Runtime requirement

WebView2, which ships with Windows 11 and current Windows 10. On a machine without it, install the
Evergreen runtime from Microsoft.

## Where things are stored

`%APPDATA%\BaudHoundTerm\`

| File | Contents |
|---|---|
| `sessions.json` | session definitions, no secrets |
| `credentials.dat` | DPAPI-encrypted secrets, keyed by session id |
| `logs\` | session transcripts |
| `webview\` | WebView2 user data |
| `crash.log` | unhandled UI exceptions |

## Known limits

- Highlight rules are applied to text as it arrives, so a match split across two reads is missed.
- The jump-host field is stored but not yet used for connection chaining.
- SSH agent authentication is selectable but not yet wired to Pageant or the Windows agent.
- Port forwarding is not implemented.
- Not tested against a hardened or non-OpenSSH server.

## Credits

This is AI-assisted work.
