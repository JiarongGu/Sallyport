# Changelog

## 0.1.0 — unreleased

- SOCKS5 server (RFC 1928 CONNECT) over a pluggable connector, loopback-only (D2) — phase 3's front half, tested in
  process; nothing dials through the tunnel yet.
- Phase 2, the data channel (2026-10-12): key method 2's PRF key expansion, `P_DATA_V2` with AES-256-GCM / AES-128-GCM,
  a replay window, keepalive and `ping-restart`, exit notices; `Tunnel` is the public surface. Measured: two DNS
  lookups answered through the tunnel in 204 ms each, and the egress address was the server's own.
- Phase 1, the control channel (2026-10-12): profile parsing, `tls-crypt` v1, the reliability layer, TLS through
  `SslStream`, key method 2 and the push request. Measured: a real server answered `PUSH_REPLY` over UDP in 1.2 s with
  no packet dropped; `Sallyport.Probe` runs it and prints nothing secret.
- Repository created (2026-10-12): an app-scoped, OpenVPN-compatible client in pure .NET, written from the protocol
  (D1), ending in a SOCKS5 proxy (D2).
