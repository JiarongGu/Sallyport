# Changelog

## 0.1.0 — unreleased

- Phase 1, the control channel (2026-10-12): profile parsing, `tls-crypt` v1, the reliability layer, TLS through
  `SslStream`, key method 2 and the push request. Measured: a real server answered `PUSH_REPLY` over UDP in 1.2 s with
  no packet dropped; `Sallyport.Probe` runs it and prints nothing secret.
- Repository created (2026-10-12): an app-scoped, OpenVPN-compatible client in pure .NET, written from the protocol
  (D1), ending in a SOCKS5 proxy (D2).
