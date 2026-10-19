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

## App-only client — route a SINGLE app through the tunnel
- [ ] A client that routes one application's traffic through Sallyport while the rest of the machine stays
      direct (a per-app VPN). Likely a new `src/Sallyport.AppRoute` project so the library stays pure: start the
      tunnel + the existing SOCKS5 server, launch or attach to the target app, and force only THAT app's sockets
      through the proxy — a per-process proxy env/setting where the app honours it, otherwise WFP (Windows
      Filtering Platform, P/Invoke, no package) to redirect just that app. Measure against a real app: its
      destination IP must read JP while another stays direct. Clean-room; no runtime deps.
