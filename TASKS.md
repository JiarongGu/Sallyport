# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 2 — the data channel (next)
- [ ] Keep both key sources from the key-method-2 exchange — phase 1 parses the server's and discards both.
- [ ] Key derivation by the TLS 1.0 PRF key expansion (D3). The test server pushed no `tls-ekm` in `protocol-flags`,
      so this is what it expects.
- [ ] `P_DATA_V2` with AES-256-GCM (what the test server pushed: `cipher AES-256-GCM`, `peer-id 1`); replay window.
- [ ] Keepalive: the server pushed `ping 12`, `ping-restart 50`, so a silent client is dropped within a minute.
- [ ] `explicit-exit-notify` on the data channel when leaving (pushed), alongside the control-channel `EXIT` already sent
      (`protocol-flags cc-exit` came back, so the server negotiated it).
- [ ] Measured by a DNS answer through the tunnel from the pushed resolver.

## Phase 3 — the userspace network and the proxy
A minimal TCP/UDP stack over the tunnel's IPv4 packets, and a SOCKS5 server on loopback that resolves names through the
VPN's DNS. Measured by an IP-echo service answering with the server's country through the proxy. Then Kobo switches its
browser route from the WSL provider to Sallyport.

## Phase 4 — the long run
Reconnect, fallback across a profile's remotes, renegotiation over hours (`ControlStream` refuses a soft reset today),
status and byte counts as events for a host (Kobo's Network page), `auth-user-pass`, `tls-auth` and `tls-crypt-v2`
profiles. Then release.

**Written but never run against a server:** the TCP transport (the test profile's second remote is `tcp/443`), and
retransmission over a real lossy path — both are covered only by in-process tests.
