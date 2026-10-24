# BaudHoundTerm: getting started

A friendly starting point. For the full detail of every setting, see the
[user manual](MANUAL.md).

BaudHoundTerm connects you to other computers and devices, each in its own tab: a server over SSH,
a Windows PC over Remote Desktop, a screen over VNC, a serial console, a network
camera, or a KVM board. It can also drive any of those for you with an AI agent.

---

## 1. Install and first run

1. Get the BaudHoundTerm `.zip` from MAH7 and **extract the whole folder** (keep
   `BaudHoundTerm.exe` together with the `Assets`, `dll`, `docs`, `libvlc` and `runtimes` folders
   next to it). This guide and the user manual are in `docs`, and the **Help** menu opens them.
2. Run `BaudHoundTerm.exe`. Windows may warn that the publisher is unknown, because the app is not
   code-signed yet: choose **More info**, then **Run anyway**.
3. The window opens empty. Everything starts from the left-hand list and the toolbar.

## 2. Your first connection

The fastest way: at the top, use **Quick connect**. Pick `SSH`, type `user@server` (or just the
address), and press **Connect**. A tab opens and asks for your password if it needs one.

To keep a connection for next time, make a **shortcut** instead:

1. Click **New** (or right-click the empty list > **New shortcut**).
2. Choose the **Protocol** (SSH, RDP, VNC, and so on). The fields below change to match it.
3. Fill in the address and login, give it a **Name**, and **Save**.
4. Double-click the shortcut in the list to connect.

You never have to save a password: leave it blank and BaudHoundTerm asks each time, which is the
safer habit. See **Passwords** below.

## 3. Finding your way around

- **The list on the left** holds your saved shortcuts, grouped into folders you make. The box above
  it filters by name, host or tag. With the 🖥 button on, each host gets a row with its shortcuts
  under it.
- **Tabs** across the top are your open connections. Each has an **×** to close it; closing a
  connected tab ends that session.
- **Switch tabs** with **Ctrl+Tab** and **Ctrl+Shift+Tab**, or **Ctrl+PgDn** and **Ctrl+PgUp**.
  Ctrl+Left and Ctrl+Right stay with the session, where shells use them to move by word.
- **Full screen** (**Terminal > Full screen**, or **F11**) hides everything but the tabs. F11 again brings it back.
- **Pop out** (**Terminal > Pop out tab**, or right-click a tab) moves the tab into its own window, still connected,
  for a second monitor. Close that window to put the tab back; F11 works in it too.
- **Hide addresses** (**Shortcut > Hide addresses**, or **Ctrl+Shift+H**) shows every IP, host and user name as `•••`,
  for demos and recordings. To always hide one shortcut, tick the box in its properties.
- **Record** (the **Record** menu, or right-click a tab) saves a video of the tab, just its terminal, or the whole window
  to `Documents\BaudHoundTerm\recordings`, with addresses hidden while it records. The same menu
  pauses and stops it. The AI agent can start and stop a recording too.
- **Several tmux sessions:** in a shortcut's properties, list more than one tmux session and each
  opens in its own tab. Right-click the shortcut or a tab for **Open tmux session** to open another.
- **Right-click** a shortcut for Connect, Properties, Move, Delete. Right-click a tab's **header**
  for its colour, properties, pop-out, and (on a desktop or KVM tab) agent options.

## 4. The kinds of connection

- **SSH / telnet / serial / local shell** are text terminals. You type, the far end answers.
  Optional **tmux** (SSH) keeps your shell alive if the link drops; a green **tmux** chip in the
  list marks shortcuts set up for it. An idle session costs the host about 20 MB and no CPU; what
  runs inside it keeps running (see the manual, "Ending a tmux session"). The mouse wheel scrolls
  back through tmux's output; hold Shift to select text.
- **RDP** opens a Windows desktop using Windows' own Remote Desktop client.
- **VNC** shows another machine's screen. Copy and paste work both ways.
- **RTSP camera** shows a camera's video, with pan/tilt/zoom if it supports it.
- **KVM** drives a machine through a [KVM board](https://github.com/info-mah7/agentkvm): the board
  acts as a USB keyboard and mouse and shows the machine's HDMI output, so nothing is installed on
  that machine. To set one up, make a KVM shortcut, enter the board's address and web login, and
  press **Pair with portal** to fetch its key automatically. On a KVM tab, **Ctrl+V** types your
  clipboard onto the machine (there is no shared clipboard), and a right-click goes to the machine.
- **BaudHound** is a pocket Wi-Fi serial console. **Shortcut > Find BaudHound devices...** finds the
  ones on your network and saves each as a shortcut.

## 5. The AI agent (optional)

The agent can work inside a tab for you: run commands on a terminal, or click and type on a VNC,
RDP or KVM desktop, toward a goal you give it.

- Open the agent from a tab's menu (**Agent on this tab**), pick an endpoint, type a goal, **Run**.
- It needs an AI endpoint you set up once in **Terminal > Agent endpoints and skills** (any
  OpenAI-compatible service, or a local model you run yourself).
- **Be careful.** Everything the agent sees goes to that AI endpoint, and the agent acts with the
  full permissions of whatever account the session is logged in as. Use a least-privilege login,
  keep the approval set to **Ask before risky**, and for sensitive systems use a local model or do
  not attach the agent at all. See the manual's **Security** section.
- Every step is logged to a file on this PC. To keep no record, untick **Keep a log of agent
  conversations** in Agent endpoints.

## 6. Moving files

On an SSH tab, **Files** opens an SFTP browser to upload and download. There is also SCP for hosts
without SFTP, and TightVNC file transfer on VNC tabs that support it.

## 7. Passwords and keys

- The safest setup is **no saved password**: leave it blank and type it when asked, or use an SSH
  key or agent.
- Saved secrets are encrypted on your PC with Windows DPAPI (tied to your Windows account) and are
  never written in readable form.
- **KeePassXC** can supply SSH passwords and keys at connect time, so nothing is stored in
  BaudHoundTerm at all. See the manual's KeePassXC section.

## 8. When something does not work

- **Can't connect:** check the address and port, and that the service is actually running. For SSH
  the first connection asks you to trust the host's fingerprint; later changes are refused until
  you accept them.
- **Agent won't run on a desktop:** its AI endpoint's model must accept images. Use **Test** in
  Agent endpoints to check.
- **Paste on a KVM tab:** use **Ctrl+V** to type your clipboard onto the machine. A big or
  multi-line paste asks first, because line breaks press Enter.
- **A skill is missing** from the agent's skill list: skills are filtered by protocol (a Windows
  desktop skill only shows on a desktop or KVM tab). If one you expect is gone entirely, open
  **Agent endpoints** and click **Restore built-in skills**.
- **Where are my files?** Settings live in `%APPDATA%\BaudHoundTerm` (shortcuts, encrypted
  credentials, logs, skills). The **Help > Open log folder** menu opens the logs.
- **Credits and licences:** **Help > Credits**, **Third-party licenses** and **License**.

---

More detail on every option is in the [user manual](MANUAL.md). To report a problem or ask a
question, use **Help > Report a problem**: it points to the [MAH7 Discord](https://mah7.com/discord).
