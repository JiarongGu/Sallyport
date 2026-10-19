# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 4 — the long run
Reconnect, fallback across a profile's remotes, renegotiation (`ControlStream` refuses a soft reset today, so a server
that renegotiates costs a reconnect; the owner's server did NOT renegotiate in a 75-minute run on 2026-10-13, though
OpenVPN's default is hourly — other servers will), status and byte counts as events for a host (Kobo's Network page), `auth-user-pass`,
`tls-auth`, `tls-crypt-v2` and `P_DATA_V1` profiles. Uploads at volume (the send side has no SACK of its own). Then release.

Kobo runs it as a provider process (`Sallyport.Host.exe serve|stop`, adopted 2026-10-12); in-process hosting waits for
the release.

**Written but never run against a server:** the reconnect path (nothing ended the tunnel in 75 minutes); whether the server acts on the exit notices
(`explicit-exit-notify`, `EXIT`). The keepalive is measured: 75 minutes up, idle between probes 5 minutes apart, never
dropped by either side. So is the TCP transport (2026-10-13, the `tcp/443` remote): TLS 1.3, DNS, ipinfo JP Tokyo,
a whole 10 MB download at 1.34 MB/s in one run (TCP inside TCP; not compared with anything).

- [ ] Allocation on the receive path. A packet is copied on its way in from the socket, again when opened, and again
      when it arrives past a hole; about 300 MB is allocated per 64 MB downloaded (D7, client included). Gen 0/1
      collections of 5–11 ms are now what stalls the network's single reader: at 64 downloads at once its queue
      peaked at 3,378 of 4,096 once in 12 runs (none overflowed). After a 64-download burst the Host held 119–147 MB
      private (14 MB idle) until the next collection — not a leak: four bursts, no growth. What holds it is not
      measured; the receive buffers, each growing toward 4 MB, are the likely part.

## The client and game routing — `docs/design/architecture.md`, `docs/design/client-ui.md`
The client works in proxy mode (D14, D15). Open:
- [ ] **Decide how a game's destinations reach the VPN (D12).** Measured: the plug-in runs, and an app filter (with an
      explicit protocol) confines a split route to the listed program — but a default route takes the whole machine,
      and a forced program cannot reach what the VPN does not route. Per-game server prefixes, known or learned from the
      game's own connections, is the shape the measurements allow. Then the real tunnel inside the plug-in: the
      program reads the server's address while another reads AU. Every run under the guard (D13).
- [ ] **What the client's measurement did not reach (2026-10-19).** The file dialogs past opening and cancelling: this
      machine's dialog shows UI Automation no File name box, and typed keys arrived mangled, so the app shortcuts were
      seeded into the settings instead. Firefox and Electron launches. The dark theme of the new window (the machine
      is light). The tray's menu and icon colours, driven by hand.
- [ ] **Ship the client**: a published build, a Start-menu entry, `.ovpn` files opened with it, start with Windows
      (`--tray`).
