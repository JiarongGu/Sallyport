# Changelog

## 0.1.0 — unreleased

- Restructured as a library with applications on it (2026-10-19, D9). `src/` the packages, `apps/` what runs
  (`Sallyport.Host`), `tools/` the probe. The wire is internal: `Opcode`, `ControlPacket`, `TlsCrypt` and `StaticKey` are
  no longer public, and the control and data channels, transports and certificates share `Sallyport.Protocol`.
  **Breaking** for in-process consumers: `Sallyport.Socks` is now `Sallyport.Proxy`, `Sallyport.Hosting` is now
  `Sallyport.Runtime`. `Sallyport` packs as a NuGet package with its own README. Kobo runs the Host and is unaffected:
  republished, its route test says JP Tokyo through the route and AU Sydney directly.
- Many connections at once (2026-10-19). The receiver keeps its SACK blocks as ranges as segments arrive — rebuilt from
  every held segment, one hole cost the network's reader 105 µs a segment and at 64 downloads it fell behind the tunnel
  and dropped thousands of packets itself. A waiting reader is woken once per burst, not per segment. A half-closed
  relay whose other side is silent for a minute is let go (D8). `TunnelCounters.Overflowed` counts packets this process
  drops itself; `Sallyport.Probe --concurrent N` measures N connections at once. Measured: 32 and 64 at once, twice,
  all whole and all from the server's address, 12.1 and 18.2 MB/s together, nothing overflowed; a cold Edge session
  (Yahoo! JAPAN, nikkei.com) through the proxy held up to 49 at once with none failing (D7).
- Phase 3, the userspace network (2026-10-12): `TunnelNetwork` — outbound TCP (MSS, window scaling, SACK, RFC 6298
  retransmission, slow start, fast retransmit, reassembly, FIN both ways) and DNS through the pushed resolvers with a TTL
  cache — and `TunnelConnector`, which makes `Socks5Server` dial through the tunnel. Measured: ipinfo.io through the
  proxy says JP Tokyo; DMM serves its domestic age check rather than the English one; a 10 MB download arrives whole at
  2.9–3.9 MB/s, level with Linux OpenVPN on the same server (D6).
- SOCKS5 server (RFC 1928 CONNECT) over a pluggable connector, loopback-only (D2).
- Phase 2, the data channel (2026-10-12): key method 2's PRF key expansion, `P_DATA_V2` with AES-256-GCM / AES-128-GCM,
  a replay window, keepalive and `ping-restart`, exit notices; `Tunnel` is the public surface. Measured: two DNS
  lookups answered through the tunnel in 204 ms each, and the egress address was the server's own.
- Phase 1, the control channel (2026-10-12): profile parsing, `tls-crypt` v1, the reliability layer, TLS through
  `SslStream`, key method 2 and the push request. Measured: a real server answered `PUSH_REPLY` over UDP in 1.2 s with
  no packet dropped; `Sallyport.Probe` runs it and prints nothing secret.
- Repository created (2026-10-12): an app-scoped, OpenVPN-compatible client in pure .NET, written from the protocol
  (D1), ending in a SOCKS5 proxy (D2).
