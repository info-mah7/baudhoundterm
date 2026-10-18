# BaudHoundTerm manual

BaudHoundTerm keeps your connections as shortcuts and opens each one in a tab: SSH, telnet,
serial, local shells, VNC desktops and RTSP cameras. This manual covers build 0.8.0-dev.30.

- [1. Install and first start](#1-install-and-first-start)
- [2. Connecting](#2-connecting)
- [3. Shortcut properties](#3-shortcut-properties)
- [4. Working in a tab](#4-working-in-a-tab)
- [5. Moving files](#5-moving-files)
- [6. BaudHound devices](#6-baudhound-devices)
- [7. Passwords, keys and KeePassXC](#7-passwords-keys-and-keepassxc)
- [8. The AI agent](#8-the-ai-agent)
- [9. Sync between PCs](#9-sync-between-pcs)
- [10. Where things are stored](#10-where-things-are-stored)
- [11. Keyboard shortcuts](#11-keyboard-shortcuts)

## 1. Install and first start

1. Download the zip and `SHA256SUMS` from the
   [releases page](https://github.com/info-mah7/baudhoundterm/releases). Builds are not code-signed
   yet, so check the zip before running it: `Get-FileHash -Algorithm SHA256 <file>` in PowerShell
   must print the hash in `SHA256SUMS` (or run `sha256sum -c SHA256SUMS` on Linux).
2. Extract the whole archive. `BaudHoundTerm.exe` needs the `Assets` and `libvlc` folders next to it.
3. Run `BaudHoundTerm.exe`. The build is unsigned, so Windows SmartScreen reports an unknown
   publisher: choose **More info**, then **Run anyway**.

It needs WebView2, which Windows 11 already has. The app starts with no tabs open. The title bar
shows the version and build number.

The window follows Windows' **Text size** setting (Settings > Accessibility > Text size), and
High Contrast themes replace its dark colours. Closing it while tabs are connected asks first,
because tabs are not reopened on the next start.

## 2. Connecting

### Quick connect

The **Quick connect** bar opens a tab without saving anything. Pick a protocol, type an address
and press Enter or **Connect**.

| Protocol | What to type |
|---|---|
| SSH | `host`, `host:port` or `user@host:port`. You are asked for the password, and for the user name if you gave none. |
| Telnet | `host` or `host:port` (23 if no port). |
| VNC | `host`, `host:port`, or `host:1` for display 1 (port 5901). |
| Local shell | A command line, e.g. `powershell.exe`. |
| Serial | The address box changes to **Port**, **Baud** and **Framing** (`8-N-1`, `7-E-1`, `7-O-1`, `8-N-2`). The port list refreshes each time you open it. |

Quick connect signs in to SSH with a password only. For a key or the SSH agent, save a shortcut.

### Shortcuts

A shortcut is a saved connection. Create one with **New** on the toolbar, **Shortcut > New
shortcut...**, the **+** above the list, or Ctrl+Shift+T.

- **Connect:** double-click it, select it and press **Connect**, or right-click > **Connect**.
- **Edit:** select it and press **Open**, or right-click > **Properties...**. (**Open** opens the
  properties; it does not connect.)
- **Folders:** right-click empty space or a folder > **New folder...**. Use `/` to nest, e.g.
  `Customers/Acme/Edge`. Right-click a shortcut > **Move to folder...** to move it; a blank name
  moves it to the top level. Deleting a folder moves its shortcuts to the top level.
- **More protocols for one host:** right-click a network shortcut > **Add protocol for this host**
  makes an SSH, VNC, telnet or camera shortcut for the same machine.
- **Filter:** the box above the list matches name, host or tag. Protocol words narrow it:
  `ssh`, `vnc`, `telnet`, `serial`, `local`, `cam`. For example `vnc lab` shows VNC shortcuts
  whose name or host contains "lab".
- **Group by host** (the 🖥 button): shortcuts in one folder that share a host sit under one row.
  Right-click that row > **Connect** to choose which one.

Open connections appear under their shortcut with their state, and clicking one brings its tab
forward. Tabs with no shortcut in view, such as quick connections, are listed under
**⚡ Open, not in the list above**.

### Local shells

**Shell ▾** opens Windows PowerShell, Command Prompt or PowerShell 7 in a tab without saving a
shortcut. The **(administrator)** entries need BaudHoundTerm itself to be elevated: if it is not,
it offers to start a second, elevated copy through UAC and open the shell there.

## 3. Shortcut properties

The properties window has seven tabs. **Save** keeps the changes.

### Connection

**Name**, **Folder**, **Tags** (comma-separated words the filter matches, such as `lab, edge`) and
**Protocol**, then the fields for that protocol.

**SSH**

- **Host**, **Port** (22), **Username**.
- **Authentication:**
  - **Password**. **Password from** is either *Saved on this PC, or ask* or *KeePassXC, looked
    up at each connect*.
  - **Public key file**, with an optional key passphrase.
  - **SSH agent (KeePassXC, Pageant, OpenSSH agent)**. The key stays in the agent, which only
    signs. **Agent key** narrows which key is offered: part of its comment or its `SHA256:`
    fingerprint. **List keys** shows what the agents hold.
  - **Keyboard interactive**, for servers that ask questions during login, such as a one-time
    code. Answered like a password, with the same sources.

  The most secure choice is **SSH agent** with your keys kept in KeePassXC: no password or key
  file is stored in BaudHoundTerm at all (see [section 7](#ssh-agent)).
- **Keep the shell running in tmux when the connection drops**, with a **tmux session name**
  (`bht`). After login the tab runs `tmux new-session -A -s <name>`, so a dropped link leaves the
  shell running on the host and the reconnect attaches to it again, mid-command. Leaving tmux
  (`exit`, or detaching with Ctrl+B D) ends the connection. The host needs tmux; without it the tab
  says so and keeps the plain shell.

**Serial**: **Port**, **Baud rate**, **Data bits**, **Parity**, **Stop bits**. Defaults are COM1,
115200, 8, None, One.

**Telnet**

- **Host**, **Port** (23; 992 for TLS).
- **TLS (telnets)**: encrypts the connection and pins the server's certificate on first use (see
  [section 7](#trusting-servers)).
- **Line mode**: for consoles that take whole lines and do not echo, such as BaudHound. Typing is
  shown and edited locally (Backspace, Ctrl+U erases the line, Ctrl+C abandons it) and each line
  is sent when you press Enter. Leave it off for routers, switches and anything with its own line
  editing.

**Local shell**: the **Command line** to run (`powershell.exe`).

**VNC**: **Host**, **Port** (5900), **Password**, and **View only,
send no input**. Classic VNC passwords are weak; use VNC only on a network you trust.

**RDP**: **Host**, **Port** (3389), **Username** (blank asks at connect), **Domain**, the password
from the vault or KeePassXC, an optional **Remote Desktop Gateway**, **Share the clipboard**,
**Share this PC's drives** (off by default: the server can then read them) and **Admin session**.
It uses Windows' own Remote Desktop client, and the remote desktop follows the tab's size.

**SQL**: **Database type** (SQL Server, PostgreSQL, MySQL/MariaDB, SQLite file), **Host**,
**Port**, **Database**, the login (or **Windows sign-in** for SQL Server), **Trust the server's
certificate** for self-signed servers, **Read-only** and **Ask before statements that change data
or structure** (on by default). Read-only is enforced by PostgreSQL, MySQL and SQLite; for SQL
Server the app refuses such statements itself. In the tab, F5 or Ctrl+Enter runs the query, or
only the selected text.

**RTSP camera**: **Camera host**, **RTSP port** (554), **Stream path** (blank asks the camera over
ONVIF), **Username**, **Password**, **RTP over TCP**, **Low latency**, and the **ONVIF port** used
for pan, tilt, zoom and stream lookup.

### Terminal

**Theme** (Quarterdeck Dark, Solarized Light, Campbell), **Font family**, **Font size**,
**Scrollback lines**, and **Tab colour** (hex like `#e06c75` or a colour name).

### Commands

Entries for the terminal's right-click menu. **Enter** runs the command rather than just typing
it; **Confirm** asks first, for anything that reboots or deletes. **Restore defaults** adds back
any missing default entries. Review the defaults before you use them; **Reboot** (`sudo reboot`) is
marked Confirm.

### Logging

**Log this session to a file** writes a transcript. The folder defaults to
`%APPDATA%\BaudHoundTerm\logs`. The file name
pattern takes `%NAME%`, `%HOST%`, `%Y%`, `%M%`, `%D%`, `%H%`, `%m%` and `%S%`. You can add a
timestamp to each line, strip colour codes (on by default), and start a new file at a set size
(64 MB). Logging works on SSH, serial, telnet and local shell tabs. Transcripts are plain text:
anything shown on screen ends up in them.

### Auto-login

Steps that wait for text (**Expect**) and then send a reply (**Send**). Tick **Secret** to send the
shortcut's password instead of the text: if none is saved, you are asked for it at that step, so
auto-login works without saving passwords. Tick **Regex** to match a regular expression. Each
step has a timeout. If a step fails, the terminal says which one and why. The steps run again
after every reconnect.

### Highlight

Colours matching output as it arrives: **Pattern**, **Colour** (`#rrggbb`), **Regex**, **Bold**,
**On**. A match split across two pieces of output is missed.

### Reconnect

Reconnects automatically when a connection drops: up to **Maximum attempts** (10, 0 means keep
trying), starting after **First delay** and growing by **Backoff factor** up to **Maximum delay**.
A rejected password or key is never retried.

## 4. Working in a tab

### Terminal tabs

| Do | How |
|---|---|
| Copy | Select, then Ctrl+Shift+C, or Shift+right-click |
| Paste | Ctrl+Shift+V, or Shift+right-click with nothing selected |
| Find in scrollback | Ctrl+Shift+F or **Find**; Enter for the next match, Escape to close |
| Clear the screen | **Clear**, or right-click > **Clear screen** |
| Reconnect a dropped tab | Press Enter in it, or **Reconnect** on the toolbar |
| Run a saved command | Right-click > the command's name |

Links in the output are clickable. **Paste image as file** (right-click) puts an image from the
clipboard on the far end and types its path: on SSH it is uploaded to `/tmp`, in a local shell it
is a temporary file. Other tab types do not support it.

**Disconnect** leaves the tab and its scrollback open; **Close** disconnects and closes it.

### Tab strip

- **Name:** the shortcut's name. The title the shell or server sets is in the tab's tooltip and on
  its row in the shortcut list.
- **Colour:** right-click a tab to pick a colour, which is saved on the shortcut. When two tabs
  open to the same machine, they get different colours automatically.
- **State dot** (serial and telnet tabs): grey disconnected, amber connecting, green connected,
  red failed. It blinks with traffic.
- **Bot icon:** attaches or detaches the AI agent (section 8).

### Link lamps

Serial and telnet tabs show lamps at the bottom right: the connection state, **TX** (sent to the
device) and **RX** (received from it), which flash with traffic, and the byte counts. A silent
serial line with a green lamp and no RX means the device is not talking; a lamp that is not
green means there is no link.

### VNC tabs

The remote desktop scales to fit the tab. A password-protected server needs the password in the
shortcut's properties. Right-click the tab > **Send Ctrl+Alt+Del** to send that key combination,
which Windows otherwise keeps for itself. With **View only** set, nothing you or the agent do is
sent to it.

### Camera tabs

Video on the left; on the right, a pan, tilt and zoom pad (arrows move while held), presets,
**Snapshot** (a PNG in `Pictures\BaudHoundTerm`) and **Mute**. The pad is greyed out when the
camera reports no PTZ over ONVIF.

## 5. Moving files

**Files ▾** (or the **Files** menu) lists what the active tab supports; the rest is greyed out with
the reason in its tooltip.

| Item | Tab | Notes |
|---|---|---|
| **Browse and transfer (SFTP)...** | SSH | A file browser with **Download** and **Upload**. Tick **Use SCP** for hosts with no SFTP, such as most network gear. |
| **Browse and transfer (TightVNC)...** | VNC | Needs TightVNC Server on the far end. |
| **Send text file...** | Any terminal | Types a text file in, paced so the far end keeps up. **Cancel** in the status line stops it. |
| **Send binary file** | Serial | YMODEM, XMODEM-1K or XMODEM. Start the receive on the device first (U-Boot `loady` / `loadx`, Linux `rb` / `rx`). |
| **Receive binary file** | Serial | YMODEM or XMODEM. Pick a folder, then start the send on the device (`sb` / `sx`). |
| **TFTP server (firmware)...** | Any | Serves a folder (`%USERPROFILE%\tftp` by default) on port 69 while the window is open. Uploads are off unless you allow them, and never overwrite. Double-click a file to copy a `tftp://` address. Windows may ask once to allow it through the firewall. |

## 6. BaudHound devices

[BaudHound](https://github.com/info-mah7/BaudHound) is a pocket ESP32-S3 serial console: clip it to
a device's UART and reach that UART over Wi-Fi.

1. Make sure the PC is on the same network as the BaudHound: either the network the device joined,
   or the device's own Wi-Fi, `BaudHound-XXXX`.
2. Press **BaudHound** on the toolbar. The window searches for about two seconds.
3. Pick the device and press **Connect to device**, or double-click it.

Each result shows the device's address, firmware, baud rate, the network it joined, and its
certificate fingerprint. **Save shortcut** adds it to the **BaudHound** folder; saving it again
after its address changed updates the existing shortcut. **Web page** opens the device's own
settings page.

- **Encrypted when possible.** Firmware 1.4 and later serve the console with TLS on port 992, and
  BaudHoundTerm uses it. Compare the certificate fingerprint with the TLS line on the device's
  Settings page the first time.
- **One session at a time.** While a TLS session is open, the device refuses other connections and
  pauses its web console. The window marks such a device **in use**.
- **Device commands** are on the tab's right-click menu: device info, detect or pin the baud rate,
  the wake probe, swap RX/TX, and an LED test. Lines starting with `~` go to the BaudHound itself;
  everything else goes to the device it is clipped to.
- **Not found?** Firmware older than 1.4 is only found when the PC is on the device's own Wi-Fi.
  Windows can also block the replies on a network marked Public. You can type the address
  instead: `192.168.4.1` on the device's own Wi-Fi, `:992` for TLS.

## 7. Passwords, keys and KeePassXC

### Do not save passwords

For the best security, do not save passwords in shortcuts. Leave the password blank and type it
when asked, look it up in KeePassXC, or use an SSH key or agent. The password prompt has a
**Remember** box: leave it unticked. Saved secrets are encrypted with
Windows DPAPI for your account: safe from other accounts and other PCs, not from programs running
as you.

### KeePassXC

1. In KeePassXC: **Settings > Browser Integration > Enable**.
2. In BaudHoundTerm: **Shortcut > Passwords and keys (KeePassXC)...**, then **Pair with open
   database**. KeePassXC asks you to name and allow the connection. (Pairing also happens by itself
   on the first lookup.)
3. Give each KeePassXC entry the URL `ssh://host`, or `ssh://host:port` for a port other than 22.
4. In the shortcut, set **Password from** to *KeePassXC*. Leave **KeePassXC entry** blank to match
   by URL, or paste the entry's UUID. **Test** looks it up without showing the password.

If the shortcut has no user name, it comes from the entry. Nothing from KeePassXC is stored by
BaudHoundTerm.

### SSH agent

BaudHoundTerm asks both Pageant and the Windows OpenSSH agent for keys. To use KeePassXC's keys,
enable **Settings > SSH Agent** in KeePassXC, choose Pageant or OpenSSH, and tick **Add key to
agent when database is opened** on the entry. The Windows agent is the service **OpenSSH
Authentication Agent**. **Refresh** in the Passwords and keys window lists the keys on offer.

### Trusting servers

The first SSH connection to a host, and the first TLS connection to a telnet server, show the
server's `SHA256:` fingerprint and ask whether to trust it. Compare it with the server's own
(`ssh-keygen -lf` on its host key, or the TLS line on a BaudHound's Settings page) before you say
yes. Later connections check it silently. If it ever changes, you get a warning with No as the
default: accept only if you know the server was reinstalled or reset.

## 8. The AI agent

The agent works in one tab for you, through any OpenAI-compatible endpoint: vLLM, llama.cpp,
Ollama, OpenRouter or OpenAI. On a terminal it types commands and reads the output. On a VNC or
RDP tab it sees screenshots and clicks and types.

> [!WARNING]
> **Everything the agent works with is sent to the endpoint's provider:** your goals, the terminal
> output it reads, remote desktop screenshots and your skills. That includes whatever is on screen:
> configuration files, passwords, keys, hostnames and addresses. A cloud provider receives all of
> it under its own terms.
>
> For better security, use a **local model** that you run yourself with vLLM, llama.cpp or Ollama,
> on your own PC or network, so nothing leaves it. For sensitive systems, use a local model or do
> not attach the agent at all.

### Set up an endpoint

**Terminal > Agent endpoints and skills...**, then **New**:

- **Name**, **Base URL** (up to `/v1`), **Model** (**Fetch models** asks the server), **API key**.
- **Temperature** and **Max steps per goal** (150 by default; on a remote desktop every click and key is a step).
- **Extra request JSON**: merged into every request. For Qwen models on vLLM,
  `{"chat_template_kwargs":{"enable_thinking":false}}` keeps replies fast, and remote desktops
  need it: with thinking on, the click check on a busy screen can use up its reply thinking and
  return nothing, and the run stops saying so. **Test** notices a model that thinks, tries the
  known switches for other servers (DeepSeek, OpenRouter, Ollama, OpenAI-style `reasoning_effort`)
  and puts the first that works into this box; **Save** keeps it.
- **Approval**: *Run freely*, *Ask before risky commands* (the default; the patterns are listed
  below it and you can edit them), or *Ask before every command*.
- **Never on local shells**: a local shell runs as you, and you can read the credential vault.
- **Remote desktops**: the screenshot width, and **Check each click on a remote desktop first**.

**Test connection** checks that the server answers, the key works, the model calls tools, and
whether it reads pictures. Terminals need tool calling; VNC and RDP tabs also need pictures.

### Use it

Attach it with the bot icon on a tab, **Terminal > Agent on this tab**, or Ctrl+Shift+A. The panel
opens on the right of the tab.

1. Pick the endpoint and tick the skills to use.
2. Type the goal and press Enter (Shift+Enter for a new line).
3. Follow its steps in the panel. **Stop** ends the run; **New** starts a fresh conversation.

**Interrupting.** While the agent works, type in the goal box and press Enter (the button reads
**Send**): the agent stops what it is waiting on, finishes any typing or click already under way,
and reads your message before its next step, ahead of the goal. Use it to correct or redirect a
run without losing what the agent has seen. **Stop** (with the box empty) or **Esc** ends the run.

When an action needs approval, a box shows exactly what it wants to run and in which tab, with No
as the default. A refused action is not retried.

On a VNC or RDP tab the agent checks each click before it happens: it names what is under the
point and must match what it meant to click, or nothing is clicked. Clicks on things like Delete or
Shut down, and keys such as Win+R, Ctrl+Alt+Del and Alt+F4, need approval under *Ask before risky
commands*. A view-only VNC tab can be looked at but not driven.

On an RDP tab the agent's clicks and keys go into the session through the Remote Desktop control,
without taking the keyboard focus from you. Keys are sent as this PC's keyboard layout, which RDP
expects to match the remote one; a character this layout cannot type stops the action with an error.

### Skills

Skills are notes the agent reads with every goal, such as house rules or how a device works. Each
is a `.md` file in `%APPDATA%\BaudHoundTerm\skills\` (**Skills folder** in the endpoints window).
In the agent pane, **Skills** opens a tree of the skills for this kind of tab, by group: tick the
ones the agent should get, or a group's box for all of them. Your ticks are kept. An optional
header sets where a skill applies:

```
---
name: Cisco console
description: How to work on the lab switches
protocols: serial, ssh
---
Always run "terminal length 0" first. Never write memory without asking.
```

`protocols` takes `ssh`, `serial`, `telnet`, `local`, `vnc` and `rdp`. A `vnc` skill is a
desktop skill and applies to RDP tabs too. Without it, the skill applies everywhere. `steps: 300`
raises the step limit for runs that use the skill, for long tasks. `group: Office` files it under
that heading (without one: Desktop, Terminal or General), and `default: off` leaves it unticked
until you tick it.

The app also ships skills from [docs/skills](skills/), added to the folder and kept up to date (a file
`.builtin` lists them: one you delete stays deleted, and one you edit is never overwritten).
**Restore built-in skills** in the endpoints window puts back any that are missing:

- **Windows desktop** (on): keyboard-first ways to open, maximize and use apps on Windows.
- **Office apps** (off): Word, Excel and PowerPoint, and the ArtCraft alternatives WordCraft,
  GridCraft and DeckCraft, which follow the same shortcuts.
- **chess.com** (off): opens chess.com in a browser and plays a bot; it raises the step limit to 300.
- **Linux admin** (off, terminal): operate and troubleshoot a Linux or BSD host over SSH, the way a
  sysadmin would, with changes made carefully and verified.
- **Network device** (off, terminal): operate and troubleshoot switches, routers and firewalls
  (Cisco, FortiGate and similar) over a console.

### Helpers

A helper is a tool the agent can call for work code does better than a model.

**Built in:** on remote desktops the agent always has `ocr_read`, the exact text in a region of the
screen, read by Windows' own OCR (Windows 10 2004 and later; it uses the languages installed in
Windows). Models misread small and thin text; this gives the agent a way to quote it exactly.

**Your own:** a helper can also be a small service on this PC that takes JSON over HTTP. List it
under `"helpers"` in `agent.json` with a `name` (the tool the model calls), the `url` the arguments
are POSTed to, a `description`, the JSON Schema of its `parameters`, and optionally `screenshot`
(send the current remote-desktop screenshot with each call), `ask` (approve every call) and
`allowRemote`. A skill names the helpers it uses in its header (`helpers: name1, name2`), and a run
gets only those both enabled there and named by a ticked skill: the model cannot add one. Helpers
must be on this PC unless `allowRemote` is set, since arguments, and screenshots if sent, would
leave the machine. Calls follow the endpoint's Approval setting and are logged like other steps.

### Logs

Every goal, model reply, action, approval and result is written to
`%APPDATA%\BaudHoundTerm\agent-logs\`, one file per day. **Log** in the panel opens the folder.

## 9. Sync between PCs

Keeps shortcuts, folders, agent endpoints, skills and trusted host keys the same on several PCs
through a private git repository. Passwords, keys, KeePassXC pairings, serial port names and key
file paths never leave the PC.

1. Install [Git for Windows](https://git-scm.com/download/win).
2. Create an **empty private** repository, on GitHub or your own server.
3. **Sync** on the toolbar (or **Shortcut > Sync between PCs (git)...**): enter the repository
   address, a branch and this PC's name, then **Connect**. A browser may open to sign in.

With **Sync automatically**, it syncs at start, a few seconds after each change, and every five
minutes. The **Sync** button shows ✓ after a good sync and ! after a failed one; its tooltip says
what happened.

- A public repository is refused: it is checked before connecting and before every push.
- Anything that looks like a password or key stops the commit.
- A shortcut changed on two PCs keeps both versions, the other one named `(conflict, PC)`.
- **Stop syncing** disconnects this PC; nothing is deleted anywhere.

## 10. Where things are stored

Everything is in `%APPDATA%\BaudHoundTerm\`:

| File | Holds |
|---|---|
| `sessions.json`, `folders.json` | Shortcuts and folders, without secrets |
| `credentials.dat` | Saved passwords, passphrases, API keys and KeePassXC pairings, encrypted with DPAPI |
| `known_hosts.json` | Trusted SSH host keys and TLS certificates |
| `agent.json` | Agent endpoints, without keys |
| `skills\`, `agent-logs\` | Agent skills and logs |
| `logs\` | Session transcripts |
| `sync.json`, `sync\` | Sync settings and the sync repository's copy |
| `ui.json` | Window preferences |
| `crash.log` | Errors the app could not handle |

Camera snapshots go to `Pictures\BaudHoundTerm`. **Help > Open log folder** opens the transcripts.

## 11. Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+Shift+T | New shortcut |
| Ctrl+Shift+F | Find in scrollback |
| Ctrl+Shift+A | Attach or detach the agent |
| Ctrl+Shift+C / Ctrl+Shift+V | Copy / paste in a terminal |
| Shift+right-click | Copy the selection, or paste when nothing is selected |
| Enter (in a dropped tab) | Reconnect |
| Enter / Shift+Enter (agent goal box) | Run / new line |
