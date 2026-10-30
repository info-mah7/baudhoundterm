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
  address in brackets, e.g. `web01 (192.168.0.10)`, and its shortcuts in that folder sit under it,
  whether there is one or several. The row takes a name its shortcuts share; when every shortcut
  has its own name (an SSH shortcut and a few tmux ones, say), it shows the host and each
  shortcut keeps its name. Right-click that row > **Connect** to choose which one; with
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
- **Run after login** (optional): a command typed into the shell once you are logged in, on
  every connect and reconnect: `claude`, `htop`, `cd ~/proj && git status`. **Presets** offers
  the same list as tmux sessions. With tmux ticked it is greyed out: each tmux session's own
  **Start command** is used instead, and only when tmux creates the session. A command that lets
  an agent act without asking marks the tab with an amber chip.
- **Jump host** (optional): the connection goes through another SSH server first, as `ssh -J`
  does. Pick one of your SSH shortcuts from the list, or type `user@host:port`. A shortcut
  brings its own login, saved password and host key, and its own jump host if it has one, so
  chains work. `user@host:port` logs in the way this shortcut does (same key, or the agent); a
  password it needs is asked for and can be remembered for this shortcut. **Host** is then the
  address as the jump host sees it. Host keys of both are checked as usual, and the file pane
  goes through the same jump.
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

  **tmux sessions** lists the sessions this shortcut uses, to start with one named after the
  shortcut ("Rebex test" gets `rebex-test`; shortcuts made before build 80 started as `bht`). tmux
  keeps one set of sessions per login on a host, so two shortcuts to the same host and login with
  the same session name open the same session; Save says so when that happens. Each one opens in
  its own tab, so one host can have a tab for work, one for logs and one for a build. Untick
  **Open on connect** for a session you want only now and then: connecting skips it, and it opens
  from the shortcut's or the tab's **Open tmux session** menu. **Remove** takes a session off the
  shortcut only; one already running on the host keeps running.

  A session can also have a **Folder** to start in (`~/work/myproject`) and a **Start command**,
  any program it runs: a coding agent, `htop`, `npm run dev`, `journalctl -f`. So one tab can always
  be one project, or one job. Both apply only when tmux starts the session: while it runs, the tab
  attaches to it as it is and nothing is typed into it. Quitting the program leaves a shell in the
  session. **Presets...** fills in the selected session's start command, editable afterwards:
  - **Claude Code: the folder's latest conversation**, `claude --continue || claude`: after the
    host restarts, opening the tab picks up where you left off; with no conversation yet it starts
    one (a conversation made with `claude -p` does not count).
  - **Claude Code: always one conversation**, kept by its id. `/clear` in Claude starts a new
    conversation, which this does not follow.
  - **Claude Code, no permission prompts**, and **Codex**, **Aider**, **htop**, **Follow the
    system log**.

  The tab shows the program's name next to its badge. When the command lets a coding agent act
  without asking (`--permission-mode bypassPermissions` or `--dangerously-skip-permissions` for
  Claude Code, `--dangerously-bypass-approvals-and-sandbox` for Codex, `--yolo` for Gemini CLI,
  `--yes-always` for Aider), the name turns amber with a ⚠. Leave those off for a tab the agent
  may attach to.

  A folder may use letters, digits, spaces and `/ . _ - ~ + @ ,`; a folder that does not exist
  starts the session in the home folder. A start command may use anything but a single quote, a
  backslash or a line break (put such a command in a script and start the script).

  **A tmux shortcut from an SSH shortcut.** Right-click an SSH shortcut and choose **Create tmux
  shortcut...**: a new shortcut to the same host and login, with tmux on and a session of its own
  named after it, and the stored password or key passphrase copied. The suggested name is free
  (`web01 tmux`, then `web01 tmux 2`), so each gets its own session. Name it in the properties
  window (the session name follows as you type), add a folder or start command if you like, and
  **Save**. It sits beside the original under the host.

  **A tmux shortcut from an open plain SSH session.** Right-click an SSH tab that is not in tmux (or
  its row in the list) and choose **Create tmux shortcut from this session...**. The properties
  window opens for a new shortcut to the same host and login with tmux on. On **Save**, a tmux
  session of its own starts on the host and the new shortcut opens on it. Nothing is typed into the
  old session: BaudHoundTerm works through a second channel of the tab's own connection.
  - **When Claude Code runs in the tab,** the new session continues its conversation: the start
    command is filled in from it (`claude --continue`, with the same permission flags), and so is
    its folder. Two Claudes on one conversation would both write it, so the new tab waits until
    Claude in the old tab has finished: type `/exit` there (the old tab's status line says so), and
    the new tab picks the conversation up. The shortcut keeps the start command, so it resumes
    Claude again after the host restarts.
  - **Otherwise,** the session's text (up to its last 5,000 lines, without colours) is copied into
    the new tmux session, which starts in the folder the tab's title shows (`user@host: ~/work`).
  - A program still running in the old tab cannot be moved: tmux holds only what it starts. Claude's
    conversation can, because Claude keeps it on disk. A tmux session of the same name that already
    runs is left alone, and you are told.

  **A shortcut from a running session.** Right-click a tab whose shell is in tmux (or its row in
  the list) and choose **Create shortcut for this tmux session...**. BaudHoundTerm asks the host
  which folder the session is in and what program runs there, and opens the properties of a new
  shortcut: this connection, named after the session, with only that session, its folder and start
  command filled in (for Claude Code, the folder's-latest form, keeping a no-prompts flag). Check
  it and **Save**; the stored password or key passphrase comes along. The new shortcut opens
  straight into that session.

**Serial**: **Port**, **Baud rate**, **Data bits**, **Parity**, **Stop bits**. Defaults are COM1,
115200, 8, None, One. The speed of a connected serial tab can be changed without reconnecting: see
[Serial speed](#serial-speed).

**Telnet**

- **Host**, **Port** (23; 992 for TLS).
- **TLS (telnets)**: encrypts the connection and pins the server's certificate on first use (see
  [section 7](#trusting-servers)).
- **Line mode**: for consoles that take whole lines and do not echo, such as BaudHound. Typing is
  shown and edited locally (Backspace, Ctrl+U erases the line, Ctrl+C abandons it) and each line
  is sent when you press Enter. Leave it off for routers, switches and anything with its own line
  editing.

**Local shell**: the **Command line** to run (`powershell.exe`).

**VNC**: **Host**, **Port** (5900), **Password**, **View only, send no input**, and **Encrypt
with TLS**. Without TLS, classic VNC passwords are weak: use plain VNC only on a network you trust.
With TLS ticked, the connection is encrypted through VeNCrypt with the server's certificate, as
TigerVNC, libvncserver and x11vnc offer it (TigerVNC: `SecurityTypes=X509Vnc`, with `X509Cert`
and `X509Key`). The first time, the certificate's fingerprint is shown and kept on a yes, like an
SSH host key; the same one later passes, a different one is refused. **Forget the server's
certificate** in the shortcut's properties makes the next connection ask again. A server that
offers TLS only without a certificate (the anonymous TLSVnc form) cannot be used from Windows:
give it a certificate. **Username** is only for a server that asks for a login as well as a
password (the X509Plain form).

**Through SSH** carries the VNC connection inside SSH: pick an SSH shortcut or type
`user@host:port`, and **Host** becomes the address as that SSH server sees it, usually
`localhost` for a VNC server that listens only there (TigerVNC's `-localhost`). Nothing of it
then crosses the network outside SSH. A typed `user@host:port` logs in with a password, asked for
once; for a key or the agent, make an SSH shortcut and pick it. TLS still works on top, and the
certificate is kept under the host and the SSH route together.

**RDP**: **Host**, **Port** (3389), **Username** (blank asks at connect), **Domain**, the password
from the vault or KeePassXC, an optional **Remote Desktop Gateway**, **Share the clipboard**,
**Share this PC's drives** (off by default: the server can then read them) and **Admin session**.
It uses Windows' own Remote Desktop client, and the remote desktop follows the tab's size.

**KVM (agentkvm board)**: a KVM board running
[agentkvm](https://github.com/mahseven/agentkvm), which types on the machine over USB and
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

**Web page (a device's admin page)**: **Address** (`https://192.168.0.99`, or a host name, which
then means https). The tab stays on that site. **The agent may change settings** is off by default:
the agent then only reads and checks (see [section 8](#the-agent-on-a-web-page)). **Forget
certificate** drops the remembered certificate, for a device whose certificate was replaced on
purpose. See [Web tabs](#web-tabs).

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
first, since each line break runs a command, unless the program in the tab has turned on
bracketed paste (Claude Code, tmux, bash 5.1 and later, zsh, vim): it then takes the paste as one
block of text and runs nothing by itself, so there is nothing to ask. **Paste image as file** (right-click) puts an image from the
clipboard on the far end and types its path: on SSH it is uploaded to `/tmp`, in a local shell it
is a temporary file. Other tab types do not support it.

A right-click always opens BaudHoundTerm's menu, even when the program in the tab uses the mouse
(tmux, vim, htop): the right button is not passed on to it. Its other buttons and the wheel are.
tmux's own menu is on its keyboard shortcut (the prefix, then `<` or `>`).

**Selecting text in tmux, vim and other programs that use the mouse.** A plain drag goes to the
program, not to a BaudHoundTerm selection; the status line says so. Two ways to copy:

- Hold **Shift** while you drag: that always makes a BaudHoundTerm selection, then Ctrl+Shift+C.
- Let the program copy: tmux, vim and Claude Code can put text on this PC's clipboard over SSH
  (OSC 52), and BaudHoundTerm accepts it on SSH and local shell tabs. The status line says each
  time. A program can only write the clipboard this way, never read it. To turn it off for a
  shortcut, untick **Programs may copy to this PC's clipboard** in its properties (Terminal).
  In tmux, `set -g set-clipboard on` makes a mouse selection copy this way.

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

### Two sessions side by side

**Split** on the toolbar, or **Ctrl+Shift+\\** (also right-click a tab > **Move to the right side**,
or **Terminal > Move tab to the other side**), moves the tab to a second strip on the right, so two
sessions show side by side. **Split** stays lit while the window is split; click it again to bring
the right side's tabs back into one strip.
Drag the divider between them to change the widths. Each side has its own tabs; the same key moves
a tab back. The side you last clicked or typed in is the active one, with a blue line along its
top: the menus and shortcuts act on its tab, and new tabs open there. **F6** switches to the other
side. When either side has no tabs left, the split closes. A split needs two tabs: with only one
open, the key does nothing but say so.

### Several tmux sessions on one host

A shortcut with more than one tmux session (section 3) opens a tab for each one ticked **Open on
connect**, all on the same host. The tab title adds the session name (`web01 · logs`). Right-click
the shortcut or a tab for **Open tmux session**: it lists the shortcut's sessions, the ones not
opened on connect marked *(on demand)*, and **New tmux session...**, which asks for a name, adds it
to the shortcut and opens it. Opening a session that is already running attaches to it, so two
tabs on the same session show the same screen.

**Running tmux sessions** lists what is actually running, whoever started it: right-click an SSH
shortcut, or a host in **Group by host** and its **Connect** menu (one list per login when the host
has several). It runs `tmux ls` on the host when the submenu opens and shows each session with its
window count, and "attached" when a client is on it. Pick one for a tab attached to it. A plain SSH
shortcut stays plain: only that tab runs in tmux. The list uses an open tab's connection to the same
login when there is one. Otherwise it logs in on its own, but only when that needs nothing typed (a
key, the SSH agent or a saved password, no jump host) and the host key is already trusted; it never
asks anything from a menu. If it cannot, it says so, and connecting first fixes it. A login the host
refuses is not tried again from the menu for 30 minutes, so browsing the list cannot lock an account. Sessions named with characters other than letters,
digits, `-` and `_` are listed but cannot be opened from here.

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

### tmux and security

A session left running keeps its shell, its scrollback and anything copied in it on the host.
Who can reach it:

- **Other users on the host cannot.** tmux runs one server per login, with its socket in a folder
  only that login can open (`/tmp/tmux-<uid>/`). From tmux 3.3 the server also refuses other users
  unless the owner lets them in with `server-access`. `tmux ls` shows only your own sessions.
- **Anything running as the same login can.** It can attach to any of that login's sessions, type
  into them and read their scrollback and paste buffers, which all its sessions share. That is
  ordinary Unix: such a process can already do whatever the login can. tmux keeps the shells in one
  place.
- **root can**, for every user's sessions.

So on a correctly set up host, tmux gives no way from one user's sessions to another's. The
exposure is what a session holds. A pane where you ran `sudo -i`, `su` or `ssh` to another host
stays logged in after the tab closes, and attaching to it asks for no password. Whoever gets into
this login, or root, gets those too.

- End a session holding a root shell, another user's shell or an onward SSH connection instead of
  leaving it detached (**End the tmux session on the host**, above).
- Keep secrets out of a session you leave running, or clear them after: `tmux clear-history` and
  `tmux delete-buffer`.
- Do not share a session socket with other users (`tmux -S` with group rights). If you must, use
  tmux 3.3 or later and give read-only access: `server-access -r <user>`.
- Sessions outlive the tab and a dropped link, not a host restart. Plugins that restore them
  (tmux-resurrect) save pane contents to disk, secrets included.

BaudHoundTerm forwards no SSH agent, so a session left running holds none of your keys.

### Sending a secret to the program in a tab

A coding agent in an SSH tab (Claude Code, Codex) sometimes needs a password, a token or a key.
Typed or pasted into its prompt, the value lands in its conversation, the scrollback and the
session log. Instead, right-click the tab (or its row in the list) and choose **Send a secret to the
program...**:

- Give it a **name**, then type the value or choose **Use a file instead** for a key file (up to 1 MB).
- BaudHoundTerm writes it to `~/.cache/baudhoundterm/secrets/<name>.<6 random characters>` on the
  host (a new file each time, so two sends of one name never touch each other) over a channel of its own, through the command's input: it is not typed into the tab, not on any command line, and
  not in the log. The folder is 700 and the file 600.
- **Delete it from the host after** 15 minutes, 1 hour (the default) or 8 hours: a background job
  on the host removes it, whether the tab is still open or not. A host that kills background jobs
  at logout, or restarts, stops that job; the next send then deletes every file past its time.
  **Keep it** leaves it for you to delete.
- With **Type a line at the prompt** ticked, the tab gets one line saying where the file is and to use
  it from there (`"$(cat <path>)"` inside a command) without printing it. Nothing is sent until you
  press Enter, so you can read or change it first. The value is never in that line.

What this does not do: anything running as that login, and root, can read the file while it is
there, as with any file of yours. And a program that reads the file into its conversation (prints
it, or `cat`s it to look) sends the value to its model provider all the same. Telling it to use the
file inside commands is what keeps it out; check the program's tool calls if it matters.

### Recording a tab

For demos, a tab can be recorded to an MP4. Start one from the **Record** menu (it acts on the
active tab), from the tab's right-click **Record**, or with **Ctrl+Shift+R**. The tab itself shows
no recording control until one runs, so a stray click on a tab never starts one:

- **This tab**: the terminal and, when it is open, the agent pane beside it. A red frame round the
  tab marks what is being recorded (it is not in the video). While another tab is in front, the
  video shows a card saying this tab is in the background, so no other session is ever recorded
  and the timeline has no gaps.
- **Whole window**: all of BaudHoundTerm, title bar included, to show the app itself. Windows marks
  it with its own yellow border. A popped-out tab records its own window.

If the video encoder refuses the full size (a large high-DPI or ultrawide window), BaudHoundTerm
records at up to 3840x2160, then tries Windows' software encoder, then half size, and the status
line says which it used. It remembers what worked for the next recording.

While it records, the tab shows a pulsing red **●**, the running time, and two buttons: **❚❚**
pauses (then **▶** resumes) and **■** stops and saves. **Ctrl+Shift+P** pauses or resumes and
**Ctrl+Shift+R** stops; the menus have **Pause recording** (or **Resume recording**) and **Stop
recording** too. The tab's row in the list shows **● REC** (or **❚❚ REC**), so a recording is
visible while its tab is not. A pause leaves nothing in the video, not even a gap. The file goes to `Documents\BaudHoundTerm\recordings`, named after the
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

The agent can record too, with its `recording` tool: start (with a scope of tab or
window), pause, resume, stop and status, so a goal like "record this tab while you check the
disks" gives a video of the run.

### Audit recording (supervised access)

For a third party, such as a vendor, given supervised access to a sensitive system, open the
**Record** menu (or right-click the tab, **Record**) and choose **Audit recording...**. Give a
folder to copy the recordings to, and the audit starts on that tab at once. The same setting is
**Audit: record every session of this shortcut** in the shortcut's properties (Connection page);
unticking it there is the only way to turn it off. From then on every tab of that shortcut:

- shows **● audit** beside the shortcut in the list, so whoever opens it knows beforehand;
- starts recording the moment it connects (**● AUDIT** and the running time on the tab and its
  row, and "This session is recorded for audit." in the status line), and keeps recording until
  the tab closes. It has no pause or stop buttons and cannot be paused or stopped, from the menus,
  the keyboard or by the agent;
- leaves addresses visible, since what was touched is the point of the record;
- on a terminal (SSH, telnet, serial, local shell), also keeps the session log, with a time on
  every line, whatever the Logging page says;
- when it ends, writes a SHA-256 list of the video and the log next to the video (in the format
  `sha256sum -c` reads), copies all of them to a folder of their own under the copy folder, and
  checks the copies against the list. A copy that fails says so in a warning, and the files stay
  in `Documents\BaudHoundTerm\recordings`.

Popping the tab out or back closes that part and starts a new one in the new window; the part that
ends with the session carries the log. Closing BaudHoundTerm ends the session first, then finishes
the files.

The record is only as safe as where it ends up: anyone who is an administrator on this PC can
change files here. Point the copy folder at a share the person at the keyboard cannot write to
after the fact (for example a NAS share that takes new files but keeps snapshots), and keep the
vendor off the PC's administrator account.

### Link lamps

Serial and telnet tabs show lamps at the bottom right: the connection state, **TX** (sent to the
device) and **RX** (received from it), which flash with traffic, and the byte counts. A silent
serial line with a green lamp and no RX means the device is not talking; a lamp that is not
green means there is no link.

### Serial speed

Right-click a connected serial tab and open **Speed**: the speed in use is ticked. Pick another (or
**Other...** for any value) and the open port switches to it at once, without closing: for a
bootloader at 115200 that hands over at 921600, say. Bytes in flight at that moment may come out
garbled. The new speed is this tab's, and a reconnect of the tab keeps it; the shortcut still
opens at its own speed until you choose **Make ... the shortcut's speed** in the same menu.

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

### Web tabs

A Web tab opens a device's admin page, such as a firewall, a switch or a hypervisor, in a browser of
its own. Add one from **New** (protocol **Web page**) or from a host's **Add protocol for this host >
Web page...**, which fills in `https://` and the host. Log in on the page yourself.

- **One site.** The tab stays on the address's scheme, host and port. A link to another site, a new
  window, and a download are refused, and the status line says so. A sign-in that sends you to
  another site (single sign-on) does not work in a Web tab.
- **Its own browser.** Each Web shortcut has its own cookies and logins, kept apart from the
  terminal pages and from other shortcuts. The page cannot reach BaudHoundTerm: it has no link to
  the app, and camera, microphone, location and notification requests are refused.
- **Certificates.** Devices nearly always have a certificate of their own, which the browser does
  not trust. The first time, BaudHoundTerm shows it with its SHA-256 fingerprint and asks: compare
  it with the device's own (on a FortiGate: **System > Certificates**). A yes remembers it for that
  host and port. The same certificate later opens without asking; a different one is refused, since
  that is what an intercepted connection looks like. **Forget certificate** in the properties
  undoes the yes.
- **←**, **⟳** and **⌂** go back, reload, and return to the shortcut's address. Hide addresses
  masks the address bar too.

**Help > Home** opens BaudHoundTerm's own pages in a Web tab: the build, the getting-started guide,
this manual, and the credits and licences, all from the files beside the exe. **Getting started**
and **User manual** open there too. Links to other sites (the MAH7 Discord, releases) open in your
own browser. The agent can be attached to it and asked how to do something; it reads these pages.

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

[BaudHound](https://github.com/mahseven/BaudHound) is a pocket ESP32-S3 serial console: clip it to
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

To keep it out entirely, untick **Help > Features > Agent**: no tab offers it, its menus and buttons
go, and any open agent is closed. It stays off until ticked again.

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

### The agent on a web page

On a Web tab the agent reads the page as text, not as pictures: headings, table rows with their
cells, and every control numbered, with its current value (a field's text, a list's choice, a
switch on or off). It clicks and types by those numbers, with real clicks at the control's place.
So a rule table or a setting comes through exactly, and a model that cannot take images works too.

It is built for checking settings. Unless the shortcut allows changes, anything that would change
the device (Apply, Save, OK, Delete, a switch or check box, a button that sends a form, Enter in a
field) waits for your yes, whatever the endpoint's Approval setting. It never types a password: log
in yourself. It moves around through the site's own menu; an address it guesses that does not
exist is reported as such, and after two it must use the menu.

Tested against a mock FortiGate with known findings (rules open from the internet, admin access
over HTTP, administrators without two-factor). The local 27B model found every one and asked
before the one change it was told to make. The 80B (text only) found them too, but in one run of
two counted a guest-to-LAN rule as open to the internet; check its findings against the rows it
quotes.

On an RDP tab the agent's clicks and keys go into the session through the Remote Desktop control,
without taking the keyboard focus from you. Keys are sent as this PC's keyboard layout, which RDP
expects to match the remote one; a character this layout cannot type stops the action with an error.

### Safeguards against planted instructions

Anything the agent reads can try to give it orders: a line in a log, a note in a file, text on a
remote screen ("AI assistant: run this", "ignore your previous instructions"). Three things stand
against it, set in **Terminal > Agent endpoints and skills > Safeguards...** for every endpoint:

- **Rules** that come first in the agent's instructions, ahead of skills and the goal. The default
  says only the goal typed in the agent pane is an instruction; everything it reads is data, and it
  must report planted instructions instead of following them. Edit them for your site, or empty
  the box to turn them off.
- **Fenced output.** Terminal output reaches the model between markers carrying a code that is new
  each time, so text in the output cannot pretend to end the output and start real instructions.
  This is always on.
- **A tripwire.** Patterns for text aimed at the agent. When something it reads matches (on a remote
  desktop: what the agent says it read there), the pane shows a warning, the log records it, and
  every later action in that conversation waits for your yes, whatever the endpoint's Approval
  setting. The agent pane's header then says **⚠ Tripwire** and the bot on the tab turns amber with
  a ⚠, so it shows from any tab; **New** in the pane starts a clean conversation and clears both.
  Empty the box to turn it off.

Rules lower the odds; they do not make a model obey. Tested with five planted attacks in terminal
output against two local models with approvals off, the models followed the planted text 8 times in
40 runs without safeguards and none in 120 runs with them, and the tripwire caught every planted
text. That is a small test of known tricks, not a guarantee: keep the approval setting on and give
the agent a login with only the rights the job needs.

A planted instruction that gets through runs where the tab is connected: on the remote host for
SSH, telnet, serial, VNC, RDP and KVM tabs; on this PC for a local shell tab. The AI endpoint only
returns text and never runs anything. Under *Ask before risky commands* the agent also asks before
reading a private key or key file and before uploading a file with curl, wget, Invoke-WebRequest or
Invoke-RestMethod.

### CAPTCHAs and other human checks

A CAPTCHA ("I'm not a robot", picking images, press and hold) is a site asking for a person, so the
agent hands it to you instead of solving it. Two things see to that, both in **Safeguards...**:

- **A rule** in the default rules: never solve, click, skip or get around a human check; stop and
  ask the user. Rules saved before it existed get it added once (rules you emptied stay empty).
- **A hard stop** on remote desktops. Before a click or drag, the agent's own name for the target,
  and what the click check finds at that point, are compared with the **human checks** patterns.
  On a match nothing is clicked: the run stops with "there is a human check on the screen" and the
  check is yours. Complete it, then tell the agent to carry on. Empty the box to turn it off.

The hard stop is there because the rule alone was not enough. On Google's reCAPTCHA demo page, the
local 27B model ticked "I'm not a robot" (and Google let it through) in 2 of 3 runs without the
rule and 1 of 5 runs with it. With the hard stop and no rule, it tried in 5 of 5 runs and was
stopped every time. A CAPTCHA reached only by the keyboard (Tab, then Space) is not seen by the hard
stop; there the rule is all that stands in the way. Merely seeing a CAPTCHA does not stop a run,
since many pages carry "protected by reCAPTCHA" at the bottom.

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

### Memory

The agent keeps notes between conversations, so it does not start from nothing on a machine it
has worked on. Two kinds of line:

- **Notes** it chooses to keep with its `remember` tool: what the device is, where things are, a
  quirk it had to work around. One fact each; the pane shows `🧠 remembered: ...` every time.
- **A line after each run:** the goal and the first line of the answer.

At the start of a conversation the agent gets the notes and the last few runs, marked as its own
past notes, not instructions, and possibly out of date.

- **Per host:** every shortcut to the same host (its SSH, tmux and web shortcuts) shares one memory,
  since they are one machine. Properties > Connection > **Keep this shortcut's notes apart** gives a
  shortcut its own. Shortcuts with no host (serial, local shells) always have their own.
- **Off:** Properties > Connection > **The agent keeps notes between runs** (on by default).
- **Memory** in the pane opens the notes as text: correct a line, delete one, or **Clear...** the
  lot. **New** starts a conversation with the notes as they are now; one already open keeps what it
  started with.
- **Never secrets:** a note that looks like it holds a password, key or token is refused, and so is
  one typed into the Memory window. Once something the agent read has tried to instruct it (the
  tripwire), nothing more is remembered in that conversation and its run line keeps no outcome. A
  memory that itself reads like instructions (edited by hand, or arrived through Sync) is left out,
  and the pane says why.

The notes are in `%APPDATA%\BaudHoundTerm\agent-memory\`, one Markdown file per host.

### Reports

Ask for a report or an export ("export this FortiGate's configuration as a report", "check the
admin accounts and give me a report") and the agent saves a Markdown file with its `save_report`
tool. The pane shows a bar with **Open**, **Save as...** and **Copy**; **Reports** opens the folder,
`Documents\BaudHoundTerm\reports\<host>\`.

- **The output is copied, not retyped.** Each command the agent runs gets an id (`o3`), and each page
  it reads one too (`p2`). In the report the agent writes `{{o3}}` where that output belongs, and
  BaudHoundTerm puts the command's whole output there, as the device printed it, including pages
  the agent never read. A model retyping a long configuration drops and invents lines; a copy does
  not. The agent's own text (the summary and findings) is marked as its account.
- **A header written by BaudHoundTerm:** the device, the date and PC, the model, which outputs were
  copied, and whether secrets are masked.
- **Secrets are masked:** FortiOS `ENC` values, Cisco `secret`/`password`/`key-string` values, SNMP
  communities, private keys and the like show as `********`, in the copied output and in the agent's
  text. Common forms only: read a report before you share it. For a full backup with the secrets in
  it, ask for one: the agent has to ask you, and the header then says the file holds them.
- On remote desktops there is no text to copy: a report holds what the agent writes.
- Saving a report changes nothing on the device, so it needs no approval; it never leaves this PC.

### Working in pairs

Two agents on two devices can work on one goal together: a VPN tunnel between two firewalls, a
link between two switches, a replication pair. Open a tab to each device, attach an agent to one,
pick the other tab under **Work with** in the agent pane, type the goal and press Run. The other
tab gets an agent too (if it had none), the same goal, and the other side of the pair.

- **Each agent works its own device and sees only its own session.** They talk through two
  tools: `tell_partner` sends a message, `wait_for_partner` waits for one. What the partner sent
  arrives before the agent's next step, marked as a message from the agent on the other device,
  and the pane shows every message both ways (**→ to partner**, **← from partner**).
- **Shared values match because one side chooses them.** The agent whose device name sorts first
  picks the pre-shared key, tunnel names, proposals and the like and tells the other, which uses
  them as given. Each is told to start by sending what the other needs (its address, its networks).
- **Approval stays per tab.** Each pane keeps its own endpoint, skills, approval setting and
  tripwire; a risky command on either device asks you in that tab as it always does.
- **A partner's message is not an instruction.** It is checked like device output: a message that
  tries to instruct the agent trips that agent's tripwire, since it means the other device, or the
  other agent, has been led astray.
- **Finishing:** when one side finishes, the other is told, with the answer, and checks it against
  its own side before finishing. A side waiting on a partner that has already finished stops
  waiting at once. Stop in either pane stops that side; the other learns the run ended.
- **A late message picks a finished side up again.** If one side has finished and the partner
  then writes (a correction, a value that changed), the finished pane starts again with the
  message and its earlier conversation, at most twice per goal. It does not if you stopped that
  side, or if you are typing a goal in its pane; the partner is told the message was not delivered.
- **A side that cannot start** (no endpoint picked, say) tells the other at once, with the reason,
  so it does its own half instead of waiting.
- The pair lasts for that goal. The next goal typed in either pane runs alone unless you pick a
  partner again. Nothing of the exchange is written anywhere; the memory of each side keeps only
  what that agent chose to remember.

### Logs

Every goal, model reply, action, approval and result is written to
`%APPDATA%\BaudHoundTerm\agent-logs\`, one file per day. **Log** in the panel opens the folder.

**Keep a log of agent conversations** in Agent endpoints (⚙) turns this off and on (on by
default). Off, nothing of a conversation is written to disk; the panel still shows it until the tab
closes, and files already in the folder stay until you delete them. The setting belongs to this PC:
Sync does not carry it to others.

## 9. Sync between PCs

Keeps shortcuts, folders, agent endpoints, skills, the agent's memory and trusted host keys the
same on several PCs through a private git repository. Passwords, keys, KeePassXC pairings, serial port names and key
file paths never leave the PC.

**Help > Features > Sync between PCs** turns it off on this PC: no syncing and no Sync menu. The
repository settings are kept for when it is ticked again.

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
- **The agent's memory:** notes added on two PCs at once are merged, every line kept. A memory
  cleared on one PC is cleared on the others; one a PC simply has not received yet deletes nothing.
  A note that looks like a secret stops the commit. **Include the agent's memory** in the Sync window
  turns it off for this PC. Reports are never synced.

## 10. Where things are stored

Everything is in `%APPDATA%\BaudHoundTerm\`:

| File | Holds |
|---|---|
| `sessions.json`, `folders.json` | Shortcuts and folders, without secrets |
| `credentials.dat` | Saved passwords, passphrases, API keys and KeePassXC pairings, encrypted with DPAPI |
| `known_hosts.json` | Trusted SSH host keys and TLS certificates |
| `web_certs.json`, `web\` | Certificates trusted in Web tabs; each Web shortcut's own browser profile |
| `agent.json` | Agent endpoints, without keys |
| `skills\`, `agent-logs\` | Agent skills and logs |
| `agent-memory\` | The agent's notes, one Markdown file per host |
| `local-shells.json` | Settings of the Shell menu's tabs (commands, font, highlights) |
| `logs\` | Session transcripts |
| `sync.json`, `sync\` | Sync settings and the sync repository's copy |
| `ui.json` | Window preferences |
| `crash.log` | Errors the app could not handle |

Camera snapshots go to `Pictures\BaudHoundTerm`, recordings to `Documents\BaudHoundTerm\recordings`, agent reports to `Documents\BaudHoundTerm\reports`. **Help > Open log folder** opens the transcripts.

**Help > Report a problem** points to the [MAH7 Discord](https://mah7.com/discord) for problems and
questions; give the build number from **Help > About**.

An elevated window keeps its browser profile in `%ProgramData%\BaudHoundTerm` instead, where
programs running as you cannot reach it.

### What BaudHoundTerm connects to

**Nothing of its own.** There is no telemetry, no analytics, no update check and no licence
check. Every connection it makes is one you asked for:

| Connection | When |
|---|---|
| SSH, telnet, serial, VNC, RDP, RTSP, a Web tab's site | A shortcut or Quick connect you open |
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
| Ctrl+Shift+R | Record this tab, or stop recording |
| Ctrl+Shift+P | Pause or resume recording |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab on the active side (also Ctrl+PgDn / Ctrl+PgUp) |
| Ctrl+Shift+\\ | Move the tab to the other side (side by side) |
| F6 | Switch to the other side |
| F11 | Full screen on or off (also in a popped-out window) |
| Ctrl+Shift+C / Ctrl+Shift+V | Copy / paste in a terminal |
| Shift+right-click | Copy the selection, or paste when nothing is selected |
| Enter (in a dropped tab) | Reconnect |
| Enter / Shift+Enter (agent goal box) | Run / new line |
