# BaudHoundTerm

Terminal and session manager for Windows 11, in the shape of SecureCRT: saved sessions in
folders, an encrypted credential store, session logging, scripted logins, auto-reconnect, and
SFTP/SCP transfer. SSH, telnet, serial and local shells. One self-contained executable, no
runtime to install.

It is also the desktop console for [BaudHound](https://github.com/info-mah7/BaudHound),
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
- **Protocols**: SSH (password, public key, agent, keyboard-interactive), telnet, serial, local
  shell, and VNC.
- **VNC viewer** in a tab beside the terminals, rendered by noVNC over a WebSocket-to-TCP bridge
  bound to 127.0.0.1 behind a per-session token. View-only is a per-session setting.
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

## Security

**Use at your own risk.** This software is provided on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied, as stated in the Apache-2.0 licence it is
released under. It handles credentials and opens sessions to production equipment. Nobody has
audited it. Satisfy yourself that it is fit for what you point it at, and assume it is not.

### Verifying a build

Binaries are unsigned, so Windows SmartScreen reports an unknown publisher on every launch.
Until there is a code-signing certificate, the checksum is the only integrity check there is.

Release 0.7.2, `baudhoundterm-0.7.2-win-x64.zip`, 59,402,284 bytes, built from commit `9902001`:

| Algorithm | Digest |
|---|---|
| SHA-1 | `b5ff15492e4647fda75ea9fa76a484b2c086a21d` |
| SHA-256 | `d1f93dc06970a535fb3511556490349a16b0c1886f04ba4e892cf4f51b22be88` |

The unpacked `BaudHoundTerm.exe` is SHA-1 `822bc59fa1483ad41cde7304d185bde891fa7e7f`.

Check a download with `Get-FileHash -Algorithm SHA1 <file>` in PowerShell, or `sha1sum` on Linux.

SHA-1 is published because it was asked for, and it is adequate for spotting a truncated or
corrupted download. It is **not** adequate against a deliberate substitution: chosen-prefix
collisions against SHA-1 have been practical since 2020. Compare the SHA-256 when the question
is whether someone tampered with the file.

### What the design does and does not protect

- Secrets are encrypted with **DPAPI** scoped to the current Windows user and never written to
  `sessions.json`. `credentials.dat` is inert on another machine or under another account. It is
  **not** protected against code already running as you, which is the limit of DPAPI.
- The **VNC bridge** listens on 127.0.0.1 only, on a port the OS chooses, and rejects any request
  whose path does not carry that session's random 128-bit token. Bytes are copied, never parsed.
  Nothing off the machine can reach it.
- **WebView2 is the renderer, not a browser.** Background networking, component update, domain
  reliability, sync, crash reporting, and DNS-over-HTTPS are all switched off at startup, and
  tracking prevention is on. A build was verified with a WebView2 net-log to make no outbound
  connections of its own. It still executes the bundled xterm.js and noVNC, so treat a tampered
  `Assets` folder as equivalent to a tampered executable.
- **Host keys**: SSH host key verification is handled by SSH.NET's defaults and there is no
  trust-on-first-use prompt or known-hosts UI yet. Do not rely on this build to detect a
  man-in-the-middle on first connect.
- **Telnet and VNC are cleartext protocols.** Passwords typed into a telnet session, and
  everything a VNC session displays, cross the network unencrypted. Tunnel them or keep them on
  a network you trust.
- Session **transcripts are written in plaintext** to `%APPDATA%\BaudHoundTerm\logs\`. Anything
  that appears on screen, including a password echoed by a careless remote prompt, lands there.

### Reporting a problem

Open an issue. If it is a vulnerability, say so in the title and leave the details out of the
public text; there is no separate disclosure channel yet.

## Known limits

- Highlight rules are applied to text as it arrives, so a match split across two reads is missed.
- The jump-host field is stored but not yet used for connection chaining.
- SSH agent authentication is selectable but not yet wired to Pageant or the Windows agent.
- Port forwarding is not implemented.
- Not tested against a hardened or non-OpenSSH server.
- SFTP/SCP transfer, auto-login scripting, and a real BaudHound device are all implemented but
  untested end to end.
- A VNC server that refuses loopback connections (the RealVNC and TightVNC default) will reject
  `127.0.0.1` even when the app runs on the same machine. Use the machine's LAN address.

## Credits

This is AI-assisted work.
