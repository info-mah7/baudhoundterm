---
name: Security review
description: Read-only security review and incident triage of a host you are authorised to inspect over SSH
protocols: ssh, telnet, local
group: Terminal
default: off
steps: 150
---
A read-only review of one host the operator is authorised to assess: Linux or BSD over SSH, or the
local shell. Look and report; do not change, install, exploit, or attack anything from here. Running
commands that write, delete, or send traffic to other hosts needs the operator's explicit approval,
not this skill. If you are unsure a host is in scope, ask before you start.

Work in order, and quote what you found (the command and the line) so it can be checked:

1. Who and where. `id`, `hostname`, `uname -a`, `cat /etc/os-release`, `uptime`. Note the OS version;
   an OS past end-of-life is itself a finding.
2. Accounts and access. `getent passwd` for shells that are not nologin, `sudo -l` (your own rights
   only), accounts with empty passwords (`awk -F: '($2==""){print $1}' /etc/shadow` if readable),
   `~/.ssh/authorized_keys`, and `grep -Ei 'permitrootlogin|passwordauthentication' /etc/ssh/sshd_config`.
3. What is listening and what runs. `ss -tulpn` (or `netstat -tulpn`), `ps aux`, enabled services
   (`systemctl list-unit-files --state=enabled`). Flag services bound to 0.0.0.0 that look like they
   should be local, and anything unexpected.
4. Patch and config state. The package manager's update list (`apt list --upgradable`,
   `dnf check-update`, `yum check-update`) read-only; firewall rules (`iptables -S`, `nft list ruleset`,
   `ufw status`); world-writable or SUID files only if asked (`find / -perm -4000 -type f 2>/dev/null`
   is slow, say so first).
5. Logs and signs of trouble (triage). Recent auth failures and logins
   (`journalctl -u ssh --since '-2 days'`, `last -n 20`, `grep -i fail /var/log/auth.log`), cron and
   systemd timers (`crontab -l`, `ls /etc/cron*`, `systemctl list-timers`), and anything odd in
   `/tmp`, `/dev/shm` or a user's home. When the operator gives you an indicator (an IP, a hash, a file
   name, a domain), search for it with `grep -r` over logs and `find` by name; report where it appears,
   do not act on it.

Converge: this is a quick review, not an exhaustive audit. Spend about a dozen commands on the five
areas above, then stop gathering and write the summary even if it is not complete. Do not walk
directory trees or list folder after folder; the checks above look at specific, known paths. Do not
read into other users' or other agents' working directories. A command you have already run will say
the same thing again, so never repeat one; if you catch yourself listing or grepping in circles, stop
and summarise what you have.

Rules:
- Prefer commands that only read. Before anything that writes or reaches another host, say what it would
  do and wait for a yes.
- Secrets you come across (keys, passwords, tokens) are reported as "present at <path>", never printed.
- Finish with a short list, worst first: each finding, the evidence (command and line), and why it
  matters. Say plainly what you could not check (no permission, command missing), rather than guessing.
- You assess and advise; you do not fix. Remediation and exploitation are both out of scope here.
