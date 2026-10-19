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
- [ ] **Game routing, next steps (`docs/design/game-routing.md` §6).** The observer is built and measured (its README):
      Zenless Zone Zero's gameplay is one UDP server plus HTTPS, learned elevated. Next: a stored per-game plan from
      observations, and the fixed-routing step — a fresh game session through the bridge with the plan's /32s,
      measured against the server. Open in the observer: short-lived children and launcher handoff; IPv6; a killed
      observer's session left running.
- [ ] **How a game is selected (D17) — the owner's choice.** Windows' VPN platform routes by destination, never by
      process alone (measured twice). Either driverless: each game's ranges, learned from its own connections (TCP from
      the owner tables; UDP needs ETW, so administrator) or supplied per game, then a real game routed and measured. Or
      a WFP callout driver of our own, as Mudfish, ExitLag and Proxifier have: C, an EV certificate, Microsoft's
      attestation signing, and a separate machine for test signing.
- [ ] **The bridge in the client.** Today `sallyport-vpn route` is a console. The client should drive it: games as an
      app kind, the guard in the client's process (D13), reconnects through `TunnelSession` rather than one `Tunnel`.
      Managing the VPN profile needs the package's identity, so either the client joins the package or it drives the
      bridge.
- [ ] **Release the package**: a code-signing certificate others trust, the loopback exemption made at install
      (administrator, once), an installer. Until then the development set-up stays on this machine: the test
      certificate in LocalMachine\TrustedPeople, the exemption, the package (`apps/Sallyport.Vpn/README.md` says how to
      remove each).
- [ ] **What the client's measurement did not reach (2026-10-19).** The file dialogs past opening and cancelling: this
      machine's dialog shows UI Automation no File name box, and typed keys arrived mangled, so the app shortcuts were
      seeded into the settings instead. Firefox and Electron launches. The dark theme of the new window (the machine
      is light). The tray's menu and icon colours, driven by hand.
- [ ] **Ship the client**: a published build, a Start-menu entry, `.ovpn` files opened with it, start with Windows
      (`--tray`).
