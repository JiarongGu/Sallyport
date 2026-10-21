# Changelog — the Sallyport package

The library on nuget.org, versioned apart from the Windows app (whose changes are in the repository's own
`CHANGELOG.md`): `nuget.yml` releases it and tags `nuget-v<version>` (docs/DECISIONS.md D36).

## 0.1.0 — 2026-10-21

The first package.

- `Tunnel.ConnectAsync`: one OpenVPN tunnel to one remote — `tls-crypt` (v1), a client certificate, a username and
  password (`auth-user-pass`) or both, TLS 1.2/1.3 through `SslStream`, AES-GCM data packets (`P_DATA_V2`), keepalive
  and exit notices, over UDP or TCP. The server's certificate is checked against the profile's `<ca>`,
  `remote-cert-tls server` and `verify-x509-name`; a profile that asks for a check this version does not make is
  refused before any packet (`Tunnel.Unsupported`).
- `TunnelNetwork`: outbound TCP and DNS over a tunnel, in userspace — no adapter, no administrator, no system route.
- `TunnelSession`: a profile's tunnel kept up as its connection policy says (remotes in rounds, `connect-retry`,
  `connect-retry-max`, `remote-random`, `ping-restart`), with `Ready`, `StatusChanged`, a `Credentials` callback asked
  before each connection, a `Log`, and `ConnectAsync`, `ResolveAsync` and `CreateHttpHandler` through whichever tunnel
  is up.
- `TunnelService`: a session behind a loopback SOCKS5 proxy on a free port (`ProxyEndpoint`), bound once the first
  tunnel is up and kept across reconnects; the session's `Ready`, status and handler beside it.
- `ProfileStore`: imported profiles sealed at rest by an `IProfileProtector` the host gives it, their sign-ins with them.
- Trimmable and ahead-of-time compatible; no dependency beyond the base class library; symbols on nuget.org with
  Source Link.
