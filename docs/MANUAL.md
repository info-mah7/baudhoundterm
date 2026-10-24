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

1. Get the zip and its `SHA256SUMS` from MAH7. Builds are not code-signed yet, so check the zip
   before running it: `Get-FileHash -Algorithm SHA256 <file>` in PowerShell
   must print the hash in `SHA256SUMS` (or run `sha256sum -c SHA256SUMS` on Linux).
2. Extract the whole archive. `BaudHoundTerm.exe` needs the `Assets`, `dll`, `libvlc` and
   `runtimes` folders next to it; `dll` holds the libraries it loads at run time. `docs` holds this
   manual and the getting-started guide, which the **Help** menu opens. `LICENSE.txt`, `NOTICE.txt`
   and `THIRD-PARTY-NOTICES.txt` give BaudHoundTerm's licence and every bundled component's;
   **Help > Credits**, **Third-party licenses** and **License** open them.
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
- **Group by host** (the 🖥 button): every host gets a row, named after the machine with its
  address in brackets, e.g. `web01 (192.0.2.10)`, and its shortcuts in that folder sit under it,
  whether there is one or several. Right-click that row > **Connect** to choose which one; with
  only one, double-clicking the row connects it. Serial ports and local shells have no host and
  stay as single rows.
- **Address details:** **Shortcut > Show address details in the list** off leaves the list with
  names only: no `user@host:port` beside a shortcut and no address beside a host row. Remembered.
- **Hide addresses** (**Shortcut > Hide addresses**, or Ctrl+Shift+H), for demos and
  screen recordings: every IP address, host name and user name BaudHoundTerm draws becomes `•••`,
  in the list, tab headers and tooltips, status lines, the agent pane and popped-out windows. Ports and shortcut
  names stay, unless a name is itself an address (quick connect's `user@host`). It is remembered,
  so a restart mid-demo stays covered. To hide one shortcut always, tick **Always hide this address
  and user name** in its properties. Only the display changes: connecting, the filter and the
  agent use the real values. Not covered: what the far end shows (a shell prompt, a remote
  desktop), the properties dialog, and the host-key question on a first connection.

Open connections appear under their shortcut with their state, and clicking one brings its tab
forward. Tabs with no shortcut in view, such as quick connections, are listed under
**⚡ Open, not in the list above**.

### Local shells

**Shell ▾** opens Windows PowerShell, Command Prompt or PowerShell 7 in a tab without saving a
shortcut. The **(administrator)** entries need BaudHoundTerm itself to be elevated: if it is not,
it offers to start a second, elevated copy through UAC and open the shell there.

What you change from such a tab (**Edit commands**, or **Properties** on the tab header) is kept
for that shell on this PC, in `local-shells.json`, and its normal and administrator tabs share it.
Builds before 70 dropped these changes when BaudHoundTerm closed.

> [!IMPORTANT]
> **Running as administrator needs BaudHoundTerm in a protected folder.** An elevated copy runs
> its own `BaudHoundTerm.exe` and loads its pages and libraries from beside it, so if anything
> running as you can change that folder, it can choose what runs as administrator. BaudHoundTerm
> therefore checks its own folder, and everything under it, before elevating:
>
> - From a folder only administrators can change, such as `C:\Program Files\BaudHoundTerm`,
>   everything works as described.
> - From anywhere else, the **(administrator)** entries are greyed out and say why, and a copy
>   started as administrator from there explains and closes.
>
> Unzip BaudHoundTerm into `C:\Program Files\BaudHoundTerm` (Windows asks for administrator
> rights to write there, which is the point) to use the administrator shells. Everything else
> works from any folder, including a USB stick.
>
> An elevated window keeps its browser profile in `%ProgramData%\BaudHoundTerm`, where programs
> running as you cannot reach it, and does not run **Sync** (it would run git, git's configuration
> and the sync repository's own settings, all of which you can change without administrator
> rights). Sync runs in the normal window, wherever BaudHoundTerm and its data are kept.

## 3. Shortcut properties

The properties window shows only the tabs and fields that apply to the protocol you pick, so you
never set an option that does nothing. A terminal session (SSH, serial, telnet, local shell) has
all of them; a desktop, camera or KVM keeps **Connection** and the tab colour, since
Commands, Logging, Auto-login, Highlight, Reconnect and the terminal font are about a text stream
it does not have. **Save** keeps the changes.

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
  says so and keeps the plain shell. A tab whose shell really is in tmux shows a green **tmux**
  chip next to its badge, and a red **no tmux** chip when it was asked for and the host has none,
  which is the case where closing the tab loses the work. The chip is checked against the host, not
  taken from this setting. The tab also turns tmux's mouse support on for that session only, so the
  wheel scrolls back through the output (tmux's own history) instead of stepping through the
  shell's command history. Hold Shift to select text with the mouse.

  **tmux sessions** lists the sessions this shortcut uses, `bht` to start with. Each one opens in
  its own tab, so one host can have a tab for work, one for logs and one for a build. Untick
  **Open on connect** for a session you want only now and then: connecting skips it, and it opens
  from the shortcut's or the tab's **Open tmux session** menu. **Remove** takes a session off the
  shortcut only; one already running on the host keeps running.

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

**KVM (agentkvm board)**: a KVM board running
[agentkvm](https://github.com/info-mah7/agentkvm), which types on the machine over USB and
watches its HDMI output. Nothing is installed on that machine and it need not be on the network;
the board is. See [section 4](#kvm-tabs) for how to set one up.

- **KVM board address** and **Daemon port** (9200): the keyboard and mouse.
- **https**: required for anything that is not a private address, because the daemon key is the
  ability to type on the machine. The certificate is checked against this PC's trust store, so the
  board needs a real one for the name you reach it by. There is no certificate pinning.
- **Daemon key**: the bearer key the board generated, kept encrypted on this PC. You rarely type
  this: **Pair with portal** fetches it for you (see below).
- **Picture from**, **Web interface username** and **Web interface password**: the board's own web
  interface, which is where the picture comes from. Blank means the same board. A single `-` means
  a board with no capture: it still types, and says so.

**RTSP camera**: **Camera host**, **RTSP port** (554), **Stream path** (blank asks the camera over
ONVIF), **Username**, **Password**, **RTP over TCP**, **Low latency**, and the **ONVIF port** used
for pan, tilt, zoom and stream lookup.

### Appearance

**Tab colour** (hex like `#e06c75` or a colour name) for any session, and for a terminal session
also **Theme** (BaudHoundTerm Dark, Solarized Light, Campbell), **Font family**, **Font size**,
**Scrollback lines** and **Mouse wheel sends Up/Down in full-screen programs** (on). In a
full-screen program that does not use the mouse (vim, less, tmux without mouse support) the wheel
moves by sending arrow keys, as other terminals do. Untick it if the wheel steps through command
history there instead.

### Commands

Entries for the terminal's right-click menu. **Enter** runs the command rather than just typing
it; **Confirm** asks first, for anything that reboots or deletes. **Restore defaults** adds back
any missing default entries. Review the defaults before you use them; **Reboot** (`sudo reboot`) is
marked Confirm. The menu shows them in this order: rearrange with **Move up** and **Move down**,
Ctrl+Up and Ctrl+Down, or by dragging a row by its left edge.

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

Links in the output are clickable and open in your default browser (http and https only; a link
whose text was set by the server shows its real address first). Pasting more than one line asks
first, since each line break runs a command. **Paste image as file** (right-click) puts an image from the
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
- **In focus:** the selected tab has a blue bar along its top, and its row in the shortcut list is
  highlighted, so with several tabs on one host you can tell which is which. When a popped-out
  window has the keyboard, its row is the highlighted one.

### Several tmux sessions on one host

A shortcut with more than one tmux session (section 3) opens a tab for each one ticked **Open on
connect**, all on the same host. The tab title adds the session name (`srv01 · logs`). Right-click
the shortcut or a tab for **Open tmux session**: it lists the shortcut's sessions, the ones not
opened on connect marked *(on demand)*, and **New tmux session...**, which asks for a name, adds it
to the shortcut and opens it. Opening a session that is already running attaches to it, so two
tabs on the same session show the same screen.

### Ending a tmux session

Closing a tab whose shell is in tmux leaves the session running on the host, which is the point of
it. To end it instead, right-click the tab (or the connection in the session list) and choose
**End the tmux session on the host**. It asks first: the session and everything running in it stop,
and the connection ends with it. The command goes over its own channel, so a full-screen program
in the session cannot swallow it.

**What a session left running costs.** Very little on its own. Measured on a Linux host, tmux and
its shell use about 20 MB of memory and no CPU while idle, and the scrollback stops growing at
tmux's `history-limit` (2,000 lines by default). With no tab attached nothing crosses the network,
and BaudHoundTerm spends nothing on it. What runs *inside* the session is the real cost: a build,
a log tail or a command-line AI tool keeps running and keeps using whatever it used before (one AI
CLI with its helpers measured about 1.2 GB). `tmux ls` on the host lists the sessions still
running; end the ones you no longer need.

### Recording a tab

For demos, a tab can be recorded to an MP4. Open the **Record** menu (it acts on the active tab), or
right-click the tab and choose **Record**:

- **This tab**: the terminal and, when it is open, the agent pane beside it.
- **Terminal only**: just the terminal (or the VNC screen), nothing around it.
- **Whole window**: all of BaudHoundTerm, title bar included, to show the app itself. A popped-out
  tab records its own window.

The tab header shows **● REC** while it records and **❚❚ REC** while paused; either menu then
has **Pause recording** (or **Resume recording**) and **Stop recording**. A pause leaves nothing in
the video, not even a gap. The file goes to `Documents\BaudHoundTerm\recordings`, named after the
shortcut (and its tmux session) and the time; **Open recordings folder** opens it. Closing the tab
or BaudHoundTerm finishes the file, and so does popping the tab out or back.

Recording uses Windows' own screen capture and video encoder (what Game Bar and the Snipping Tool
use), so nothing is installed and nothing leaves the PC. Other windows on top of BaudHoundTerm are
not in the video, nor is the mouse pointer. The video runs at 15 frames a second, which suits text.
Windows 11 draws a yellow outline round the window while it is captured; the outline is not in the
file.

**Hide addresses** turns on by itself while a recording runs and goes back to how it was at the
end, so the list, tab titles, status line and agent pane show `•••`. It cannot mask what the host
itself prints: a prompt like `user@host:~$` is in the terminal as the host sent it. For a demo, use
a demo account or set a neutral prompt (`PS1='$ '`).

The agent can record too, with its `recording` tool: start (with a scope of terminal, tab or
window), pause, resume, stop and status, so a goal like "record this tab while you check the
disks" gives a video of the run.

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

### KVM tabs

A KVM tab shows what the board sees and sends what you type and click to the machine over USB,
with no software on that machine.

To set one up:

1. Install agentkvm on the board and note the key it generates. Its README has the steps for each
   supported board.
2. In BaudHoundTerm, **New shortcut** > **Protocol: KVM (agentkvm board)**.
3. Fill in the board's address, the web interface username and password, and tick **https** if the
   board is not on a private network. Leave the port at 9200 unless you changed it.
4. Press **Pair with portal**. BHT signs in to the board's web interface with that login and
   fetches the daemon key for you, so you never copy it by hand. The status line says when it is
   paired.
5. Connect.

Pairing is the whole of the setup: one login sets both the key (for the keyboard and mouse) and
the picture. If you already have the daemon key, you can type it into **Daemon key** instead and
skip pairing. Each board is added by hand, the same as any other shortcut; there is no discovery
or DNS to configure.

The keyboard is a **US layout**. The board sends key positions, not characters, so a machine set
to another layout receives some characters wrong, and there is no way to read its layout through a
USB keyboard.

The agent attaches to a KVM tab as it does to VNC and RDP, with the same approval settings,
skills and close-up check before a click (see [section 8](#8-the-ai-agent)).

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
2. Choose **Shortcut > Find BaudHound devices...**. The window searches for about two seconds.
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
- **Extra request JSON**: merged into every request, for anything the settings do not cover.
- **Thinking**: *as the server has it*, *off*, or *on*. Thinking models plan before answering,
  which helps on hard steps and costs time on **every** step; on a remote desktop the click check
  waits for it too, and on a busy screen it can spend its whole reply thinking and return nothing,
  which stops the run. There is no standard switch, so BaudHoundTerm learns the one this server
  takes (Qwen, DeepSeek, OpenRouter, Ollama, OpenAI-style `reasoning_effort`) and sends it with
  every request.
- **Context window in tokens**: how much conversation to keep. 0 uses a safe default of about
  32k tokens.

**Tune for this model** measures the model on your own server rather than guessing, and fills both
in:

- the real context window, as the server reports it;
- which switch turns thinking off and on here, and how long a short answer takes each way;
- how long the model takes to re-read the conversation, which is the part people miss. The model
  reads the whole history on **every** step, so the window the server allows is not the window
  worth using. On a 27B measured here, reading costs about 0.4 s per 1,000 tokens: a full
  131,072-token conversation would add about a minute to every step, so Tune suggests about
  18,000 tokens, which keeps that share near 8 seconds. A faster server (the 80B here, about
  0.1 s per 1,000) is offered nearly its whole 65k window.

Raise the context for a long task that genuinely needs the history, and expect slower steps.
**Save** keeps what Tune found.
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

Under *Ask before risky commands* a built-in list applies on top of the endpoint's own patterns
and cannot be removed: deleting recursively or by wildcard (del /s, rmdir /s, Remove-Item -r or
-Force), deleting shadow copies, adding users or administrators, `iex` and `-EncodedCommand`,
piping a download into a shell, turning off Defender or the firewall, stopping services and
processes, scheduled tasks, registry and permission changes, disk changes, and any command that
touches BaudHoundTerm's own settings, the credential store or `authorized_keys`. Shell escapes
(`^`, backticks, quotes, look-alike dashes) do not hide a command from it.

In a BaudHoundTerm opened as administrator, an agent on a local shell asks before **every**
command, whatever the endpoint's Approval setting, and the box says the shell runs as
administrator.

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

`protocols` takes `ssh`, `serial`, `telnet`, `local`, `vnc`, `rdp` and `kvm`. A `vnc` skill is a
desktop skill and applies to RDP and KVM tabs too, so the Windows desktop skill works on a KVM
unchanged. Without it, the skill applies everywhere. `steps: 300`
raises the step limit for runs that use the skill, for long tasks. `group: Office` files it under
that heading (without one: Desktop, Terminal or General), and `default: off` leaves it unticked
until you tick it.

The app also ships built-in skills (the files in `docs/skills` in its source), added to the folder and kept up to date (a file
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
- **BIOS and UEFI setup** (off, KVM only): enter and navigate a machine's firmware setup over a KVM,
  before any OS runs, which no other protocol can reach. Keyboard-first, vendor-aware, and careful:
  it reads the current value and asks before changing anything that can stop a machine booting.

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

**Keep a log of agent conversations** in Agent endpoints (⚙) turns this off and on (on by
default). Off, nothing of a conversation is written to disk; the panel still shows it until the tab
closes, and files already in the folder stay until you delete them. The setting belongs to this PC:
Sync does not carry it to others.

## 9. Sync between PCs

Keeps shortcuts, folders, agent endpoints, skills and trusted host keys the same on several PCs
through a private git repository. Passwords, keys, KeePassXC pairings, serial port names and key
file paths never leave the PC.

1. Install [Git for Windows](https://git-scm.com/download/win).
2. Create an **empty private** repository, on GitHub or your own server.
3. **Help > Sync between PCs (git)...**: enter the repository
   address, a branch and this PC's name, then **Connect**. A browser may open to sign in.

With **Sync automatically**, it syncs at start, a few seconds after each change, and every five
minutes. The menu item shows ✓ after a good sync and ! (in amber) after a failed one; its tooltip
says what happened.

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
| `local-shells.json` | Settings of the Shell menu's tabs (commands, font, highlights) |
| `logs\` | Session transcripts |
| `sync.json`, `sync\` | Sync settings and the sync repository's copy |
| `ui.json` | Window preferences |
| `crash.log` | Errors the app could not handle |

Camera snapshots go to `Pictures\BaudHoundTerm`, recordings to `Documents\BaudHoundTerm\recordings`. **Help > Open log folder** opens the transcripts.

**Help > Report a problem** points to the [MAH7 Discord](https://mah7.com/discord) for problems and
questions; give the build number from **Help > About**.

An elevated window keeps its browser profile in `%ProgramData%\BaudHoundTerm` instead, where
programs running as you cannot reach it.

### What BaudHoundTerm connects to

**Nothing of its own.** There is no telemetry, no analytics, no update check and no licence
check. Every connection it makes is one you asked for:

| Connection | When |
|---|---|
| SSH, telnet, serial, VNC, RDP, RTSP | A shortcut or Quick connect you open |
| A git remote | Only while sync runs, to the repository you configured |
| An AI endpoint, and helpers you declare | Only while an agent runs, to the URL you set |
| KeePassXC, the SSH agent | Local pipes, when a session needs a secret |
| UDP broadcast on the local network | Only while the BaudHound window searches for devices |
| TFTP | Only while you run the built-in server |

The terminal and VNC views are drawn by Microsoft's WebView2, which is Chromium and would
otherwise contact Microsoft on its own (component updates, field trials, SmartScreen, and
DNS-over-HTTPS to a public resolver). All of that is switched off when the browser starts, and
the pages are served from BaudHoundTerm's own files, not the web.

Checked on Windows 11 for build 60, with a local shell tab open and no session connected: over
several minutes the app and its whole process tree made **no outbound connection and looked up no
name**, and Chromium's own network log recorded **no request attempted**. The only name it looks
up is `wpad`, which is Windows' proxy auto-discovery, part of the system's proxy settings rather
than anything BaudHoundTerm sends.

## 11. Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+Shift+T | New shortcut |
| Ctrl+Shift+F | Find in scrollback |
| Ctrl+Shift+A | Attach or detach the agent |
| Ctrl+Shift+H | Hide addresses on or off |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab (also Ctrl+PgDn / Ctrl+PgUp) |
| F11 | Full screen on or off (also in a popped-out window) |
| Ctrl+Shift+C / Ctrl+Shift+V | Copy / paste in a terminal |
| Shift+right-click | Copy the selection, or paste when nothing is selected |
| Enter (in a dropped tab) | Reconnect |
| Enter / Shift+Enter (agent goal box) | Run / new line |
