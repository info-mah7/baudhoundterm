---
name: FortiGate review
description: Read and summarise a FortiGate (FortiOS) firewall config, and flag risky settings
protocols: ssh, serial, telnet
group: Terminal
default: off
steps: 120
---
Read-only review of a FortiGate you are authorised to inspect. Never change the running config,
commit, reboot or fail over; those need the operator's explicit go-ahead, not this skill.

Getting the config:
- First widen the pager so nothing is cut off: `config system console`, `set output standard`, `end`.
- Whole config: `show full-configuration`. It is long; read it in sections instead.
- One section at a time is easier: `show firewall policy`, `show firewall address`,
  `show firewall service custom`, `show system interface`, `show system zone`, `show router static`,
  `show firewall vip`, `show system admin`, `show vpn ipsec phase1-interface`.
- On a VDOM box, pick the VDOM first: `config vdom`, `edit <name>`; `end` to leave it. `show system vdom`
  lists them. The global parts are under `config global`.

The block format: `config <section>` opens it, `edit <name/id>` one entry, `set <key> <value>` a field
(values with spaces are quoted, lists are space-separated), `next` ends an entry, `end` the section.
A policy's `srcintf`/`dstintf` are interfaces or zones, `srcaddr`/`dstaddr`/`service` are named objects
defined elsewhere, and `action accept|deny`, `nat enable` and the UTM profiles (`av-profile`,
`ips-sensor`, `webfilter-profile`) decide what it does.

When you summarise, quote the setting you mean (section and entry) so the operator can find it. Call
these out when you see them:
- A policy with `srcaddr all` and `dstaddr all` and `action accept`, or `service ALL`: an any-any rule.
- `set action accept` with no logging (`set logtraffic` off or absent).
- Management open to the internet: an interface `allowaccess` including `http`, `telnet`, `ssh` or
  `https` on a WAN interface; admin with `trusthost` unset (reachable from anywhere).
- `set status disable` on a policy that looks important, or disabled security profiles.
- Weak admin: password policy off, no two-factor on admin accounts, `http`/`telnet` admin access.
- Old or `set type tunnel` VPNs with weak proposals (`des`, `md5`, DH group 1/2/5).
- Expired or soon-to-expire certificates and licences, and firmware well behind current.

Do not print full pre-shared keys, passwords or private keys you come across; say the field is set and
move on. If asked to compare against a standard, name the checks you ran and what you could not see.
