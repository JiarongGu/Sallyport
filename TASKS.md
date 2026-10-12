# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 1 — the control channel (in progress)
- [ ] Profile: remotes, `<ca>`, `<cert>`, `<key>`, `<tls-crypt>` (v1), cipher, `auth-user-pass` flag.
- [ ] `tls-crypt` v1: wrap and unwrap control packets (AES-256-CTR + HMAC-SHA256, the client's key direction).
- [ ] Hard reset handshake: `P_CONTROL_HARD_RESET_CLIENT_V2` → `..._SERVER_V2`, session ids.
- [ ] Reliability: message packet ids, ACKs (piggybacked and `P_ACK_V1`), in-order delivery, retransmission.
- [ ] TLS over the control channel (`SslStream`), client certificate from the profile, server checked against `<ca>`
      and for the server-auth key usage.
- [ ] Key method 2 exchange, then `PUSH_REQUEST` → **`PUSH_REPLY` from a real server** (the phase's measurement).

## Phase 2 — the data channel
Key derivation (the TLS 1.0 PRF key expansion — .NET exposes no TLS exporter, D3), `P_DATA_V2` with the negotiated
cipher (AES-256-GCM, or CBC + HMAC), replay window, keepalive, renegotiation. Measured by a DNS answer through the tunnel.

## Phase 3 — the userspace network and the proxy
A minimal TCP/UDP stack over the tunnel's IPv4 packets, and a SOCKS5 server on loopback that resolves names through the
VPN's DNS. Measured by an IP-echo service answering with the server's country through the proxy. Then Kobo switches its
browser route from the WSL provider to Sallyport.

## Phase 4 — the long run
Reconnect, TCP transport and fallback across a profile's remotes, renegotiation over hours, status and byte counts as
events for a host (Kobo's Network page), `auth-user-pass` profiles. Then release.
