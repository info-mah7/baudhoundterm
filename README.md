# BaudHoundTerm

Agentic, multi-protocol terminal and session manager for Windows 11: saved sessions in folders,
an encrypted credential store, session logging, scripted logins, auto-reconnect, SFTP/SCP
transfer, and an AI agent that can work in any tab. SSH, telnet, serial, local shells, VNC and
cameras. One self-contained executable, no runtime to install.

It is also the desktop console for [BaudHound](https://github.com/info-mah7/BaudHound),
the pocket ESP32-S3 wireless serial tap: BaudHound puts a UART on the network over ssl, and
BaudHoundTerm gives that connection saved sessions, logging, highlight rules and reconnect.

Apache-2.0.

## Why it does not wrap Windows Terminal

The obvious design is to shell out to `wt.exe` with a generated profile. It cannot work for this
feature set: Windows Terminal owns the pseudoconsole and exposes no way to read the output stream
or inject into the input stream. Everything interesting here (logging a transcript, colouring
matching lines, waiting for a password prompt and answering it, searching the scrollback) needs
the stream. So Quarterdeck hosts the terminal itself: a ConPTY for local shells, SSH.NET channels
for SSH, and a serial port for consoles, rendered by xterm.js.

The [manual](docs/MANUAL.md) explains how to use it.

## Features

- **Sessions** in a folder tree, filterable by name, host or tag; clone any session with its
  stored credentials.
- **Credential vault** encrypted with DPAPI under the current user. Secrets never enter
  `sessions.json`; the file is useless on another machine or account.
- **Protocols**: SSH (password, public key, agent, keyboard-interactive), telnet, serial, local
  shell, and VNC.
- **KeePassXC**: SSH passwords can be looked up in KeePassXC at each connect (never stored here),
  and agent logins sign through Pageant or the Windows OpenSSH agent, where KeePassXC loads its keys.
- **Sync between PCs** through a private git repository: shortcuts, folders, agent endpoints,
  skills and trusted host keys, one file per shortcut. Needs Git for Windows; sign-in is git's
  own (browser for GitHub, keys for ssh). A public repository is refused, checked before every
  push by reading it anonymously. Secrets, KeePassXC pairings, serial port names and key file
  paths stay on each PC. Conflicts keep both versions.
- **VNC viewer** in a tab beside the terminals, rendered by noVNC over a WebSocket-to-TCP bridge
  bound to 127.0.0.1 behind a per-session token. View-only is a per-session setting.
- **AI agent** on any tab, through an OpenAI-compatible endpoint (vLLM, llama.cpp, Ollama,
  OpenRouter). On a terminal it types commands and reads the output; on a VNC tab it sees
  screenshots and clicks and types. Risky actions can need your approval, and every step is
  logged.
- **Session logging** to file, optionally timestamped per line and stripped of ANSI sequences,
  with size-based rolling.
- **Auto-login scripting**: expect/send steps with literal or regex matching and a timeout per
  step, re-run after a reconnect. The password comes from the vault, never from the script text.
- **Auto-reconnect** with exponential backoff and an attempt cap.
- **File transfer**: SFTP browser with upload and download, or SCP for hosts with no SFTP
  subsystem. Remote paths are shell-quoted.
- **BaudHound**: the BaudHound button finds devices on the networks the PC is on (UDP broadcast,
  firmware 1.4+; older firmware is found when the PC has joined the device's own AP) and opens
  one over TLS (port 992, certificate pinned on first use) when the device has it, in line mode, with the device's `~` commands on the right-click menu. Save
  it as a shortcut; saving again after a DHCP change moves the shortcut to the new address.
- **Telnet over TLS** (telnets) for devices with self-signed certificates: the certificate's
  SHA-256 is pinned on first use in the same store as SSH host keys, and a change is refused
  unless you accept it.
- **Line mode** for telnet consoles that read whole lines and echo nothing: typing is echoed and
  edited locally and each line goes out with CR LF.
- **Link lamps** on serial and telnet tabs: connection state, TX and RX activity and byte counts
  in the status bar, and a state dot on the tab that blinks with traffic.
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

## BaudHound over TLS

BaudHound firmware 1.4 and later serve the console encrypted on port 992. The **BaudHound**
button uses it whenever the device offers it; a saved session can turn it on with **TLS
(telnets)** in its telnet settings.

1. The first connection shows the device's certificate fingerprint, `SHA256:...`. Compare it
   with the TLS line on the device's **⚙ Settings** page, then trust it.
2. Later connections check it silently. If it changes, the connection stops with a warning. That
   is expected after the device's flash is erased; at any other time, refuse it.
3. While the session is open, the device accepts no other connection, and its web console and
   plain telnet port are paused. The picker marks such a device "in use". Closing the tab frees
   it; a PC that drops off the network frees it after about 25 seconds.

The device asks for no password: whoever connects first holds it. The firmware's
[docs/TLS.md](https://github.com/info-mah7/BaudHound/blob/main/docs/TLS.md) has the
details.

## Agent on a remote desktop

Attach the agent to a VNC tab and it works the desktop through that tab's own connection:
screenshots in, pointer and key events out. Nothing is installed on the remote machine.

- **One model does everything**, so the endpoint's model must accept images and call tools.
  **Test** in Agent endpoints checks both. On vLLM, Qwen3.8-27B works with its vision tower
  enabled.
- Positions are on a 0-1000 grid over the screen, so the screen's resolution does not matter.
  Screenshots are scaled to 1280 px wide by default (endpoint setting).
- **Clicks are checked first.** The model names what is under a crosshair on a close-up of the
  point, and that has to match what it said it was clicking. If not, nothing is clicked and it
  is told what is there instead.
- Only the latest screenshot stays in the conversation. Each action carries a note of what the
  screen showed, so text the agent reports comes from the screen, not from memory.
- **Approval:** under "Ask before risky", clicks on things like Delete, Uninstall or Shut down,
  typed text matching the risky patterns, and combinations such as Win+R, Ctrl+Alt+Del and
  Alt+F4 need a yes. A view-only session can be looked at but not driven.

## Where things are stored

`%APPDATA%\BaudHoundTerm\`

| File | Contents |
|---|---|
| `sessions.json` | session definitions, no secrets |
| `known_hosts.json` | trusted SSH host keys and TLS certificate fingerprints |
| `credentials.dat` | DPAPI-encrypted secrets, keyed by session id |
| `logs\` | session transcripts |
| `webview\` | WebView2 user data |
| `crash.log` | unhandled UI exceptions |
| `sync.json` | this PC's sync settings and per-PC values (never synced) |
| `sync\` | the git clone used for sync |

## Security

**Use at your own risk.** This software is provided on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied, as stated in the Apache-2.0 licence it is
released under. It handles credentials and opens sessions to production equipment. Nobody has
audited it. Satisfy yourself that it is fit for what you point it at, and assume it is not.

**Do not save passwords in your connection profiles.** For the best security, leave the password
blank and type it when asked at each connect, look it up in KeePassXC, or log in with an SSH key
or agent. A saved password is only as safe as your Windows account (see DPAPI below).

**The AI agent sends your session to its model provider.** Everything the agent works with goes
to the endpoint you configure: your goals, the terminal output it reads, screenshots of VNC
desktops, and your skills. That includes whatever is on screen at the time: configuration files,
passwords, keys, hostnames and addresses. If the endpoint is a cloud service, that provider
receives all of it under its own terms.

For better security, use a local model: one you run yourself with vLLM, llama.cpp or Ollama, on
your own PC or network, so nothing leaves it. For sensitive systems, use a local model or do not
attach the agent at all.

### Verifying a build

Binaries are unsigned, so Windows SmartScreen reports an unknown publisher on every launch.
Until there is a code-signing certificate, the checksum is the only integrity check there is.

Build 0.8.0-dev.30, `baudhoundterm-0.8.0-dev.20261014.30-win-x64.zip`, 75,114,466 bytes, built
from commit `df17bb9` (tag `v0.8.0-dev.30`):

| Algorithm | Digest |
|---|---|
| SHA-1 | `29fa34f76d0d5806d7edfa34dcc780989570c825` |
| SHA-256 | `b5c87783315f7eb4714be0c760ba5c989c85bea94a8c31d0b561b70c6e7817bd` |

The unpacked `BaudHoundTerm.exe` is SHA-1 `15d5c8cf4e2f5b1f0240b14e50cf4dab6e0f7b6e`.

Build 0.8.0-dev.29, `baudhoundterm-0.8.0-dev.20261014.29-win-x64.zip`, 75,113,921 bytes, built
from commit `80f0d2d` (tag `v0.8.0-dev.29`):

| Algorithm | Digest |
|---|---|
| SHA-1 | `16e99865baa953257e8294be3ad2fbe607c5cde9` |
| SHA-256 | `61be88a386956a4aca440397faed722fd6e037f0e089c75ac4ccf3e65a65c8ce` |

The unpacked `BaudHoundTerm.exe` is SHA-1 `b90328b1581bd99382ff8f84ca538379e38eb19f`.

Each release also carries a `SHA256SUMS` file. Check a download with `sha256sum -c SHA256SUMS` on
Linux, or `Get-FileHash -Algorithm SHA256 <file>` in PowerShell and compare with the table.

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
- **Host keys and TLS certificates** are trusted on first use, like OpenSSH: the first
  connection shows the SHA-256 fingerprint and asks, and a later change is refused unless you
  accept it. Pins are kept in `known_hosts.json`. The first connection is only as safe as your
  check of that fingerprint against the server or device.
- **Telnet and VNC are cleartext protocols.** Passwords typed into a telnet session, and
  everything a VNC session displays, cross the network unencrypted. Tunnel them or keep them on
  a network you trust. The exception is telnet with **TLS** turned on (telnets, used for
  BaudHound on port 992), which is encrypted and pinned as above.
- Session **transcripts are written in plaintext** to `%APPDATA%\BaudHoundTerm\logs\`. Anything
  that appears on screen, including a password echoed by a careless remote prompt, lands there.

### Reporting a problem

Open an issue. If it is a vulnerability, say so in the title and leave the details out of the
public text; there is no separate disclosure channel yet.

## Known limits

- Highlight rules are applied to text as it arrives, so a match split across two reads is missed.
- SSH jump hosts are not supported yet.
- Port forwarding is not implemented.
- Not tested against a hardened or non-OpenSSH server.
- SFTP/SCP transfer, auto-login scripting, and a real BaudHound device are all implemented but
  untested end to end.
- A VNC server that refuses loopback connections (the RealVNC and TightVNC default) will reject
  `127.0.0.1` even when the app runs on the same machine. Use the machine's LAN address.

## Credits

This is AI-assisted work.
