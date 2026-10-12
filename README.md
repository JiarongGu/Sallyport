# Sallyport

**An app-scoped, OpenVPN-compatible client in pure .NET — a VPN for one application, not the machine.**

A sally port is a fortress's small, controlled gate: it lets chosen people through without opening the main gates.
Sallyport does that for network traffic. It speaks the OpenVPN protocol to an ordinary OpenVPN server, keeps the tunnel
inside the process that uses it, and ends it in a local SOCKS5 proxy. Point an application — or a browser, for the sites
that need it — at that proxy, and only that traffic goes through the VPN. No network adapter, no administrator rights,
no system routes, no WSL.

## Status

Phases 1 and 2 of 4 done (2026-10-12): against a real OpenVPN server, the control channel reaches `PUSH_REPLY`
(`tls-crypt`, client certificate, TLS 1.3, 1.1 s) and the data channel carries DNS both ways (AES-256-GCM). Phase 3, the
userspace TCP and the SOCKS5 proxy, is next (see `TASKS.md`) — until then it carries packets, not connections. First consumer: [Kobo](../Kobo/apps/kobo), whose browser route uses it before it is released.

## Licence and provenance

MIT. **Written from the OpenVPN protocol's public description, not from OpenVPN's source code** — OpenVPN 2 is GPLv2 and
OpenVPN 3 is AGPLv3, and neither's code is used, ported or translated here (`docs/DECISIONS.md`, D1).

"OpenVPN" is a registered trademark of OpenVPN Inc. Sallyport is an independent, OpenVPN-*compatible* implementation
and is not affiliated with or endorsed by OpenVPN Inc.

## Building

```
dotnet build Sallyport.slnx
dotnet test Sallyport.slnx
dotnet run --project src/Sallyport.Probe -- <profile.ovpn>     # phase 1: connect and print the server's PUSH_REPLY
```

A profile holds a private key: it is read from wherever the caller keeps it and never copied into this repository.
