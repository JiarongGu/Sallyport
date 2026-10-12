# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 3 — the userspace network and the proxy (next)
- [ ] Merge the SOCKS5 server (`feat/socks5`, built in a worktree, reviewed before merge).
- [ ] A packet dispatcher over `Tunnel.Packets`: UDP to the resolver, TCP to its connection. `TunnelDns` reads the
      packets itself today, which only works while nothing else does.
- [ ] A resolver through the pushed DNS servers, with a cache that honours TTLs; names never go to the machine's resolver.
- [ ] A minimal TCP (active open only): handshake with MSS, sequence and ACK, retransmission with a measured RTO,
      out-of-order reassembly, a receive window, FIN in both directions, RST. Enough for a browser, not a general stack.
- [ ] `TunnelConnector : IConnector` joining the two, so `Socks5Server` dials through the tunnel.
- [ ] Measured by an IP-echo service answering through the proxy with the server's country.

## Then Kobo adopts it
Kobo hosts Sallyport in-process in place of the WSL `start`/`stop` commands; the route file's `proxy` and `hosts` stay.
Measured by a FANZA page in Kobo's browser with WSL's OpenVPN stopped.

## Phase 4 — the long run
Reconnect, fallback across a profile's remotes, renegotiation over hours (`ControlStream` refuses a soft reset today —
the server's default is hourly), status and byte counts as events for a host (Kobo's Network page), `auth-user-pass`,
`tls-auth`, `tls-crypt-v2` and `P_DATA_V1` profiles. Then release.

**Written but never run against a server:** the TCP transport (the test profile's second remote is `tcp/443`);
retransmission over a real lossy path; the keepalive over minutes (the live runs lasted seconds); whether the server
acts on the exit notices (`explicit-exit-notify`, `EXIT`). Each is covered only by in-process tests, or not at all.
