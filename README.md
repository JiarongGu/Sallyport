# Sallyport

**An app-scoped, OpenVPN-compatible client in pure .NET — a VPN for one application, not the machine.**

A sally port is a fortress's small, controlled gate: it lets chosen people through without opening the main gates.
Sallyport does that for network traffic. It speaks the OpenVPN protocol to an ordinary OpenVPN server, keeps the tunnel
inside the process that uses it, and ends it in a local SOCKS5 proxy. Point an application — or a browser, for the sites
that need it — at that proxy, and only that traffic goes through the VPN. No network adapter, no administrator rights,
no system routes, no WSL.

## Status

Phases 1–3 of 4 work against a real OpenVPN server (2026-10-12): the control channel (`tls-crypt`, client certificate,
TLS 1.3), the data channel (AES-256-GCM), and a userspace TCP behind a loopback SOCKS5 proxy that downloads as fast as
Linux OpenVPN does on the same server, and carries a browser page's dozens of connections at once (2026-10-19, D7).
Phase 4 is what a long-running client needs (see `TASKS.md`). First consumer: [Kobo](../Kobo/apps/kobo), whose browser
route runs `Sallyport.Host` before it is released.

## The client

`apps/Sallyport.Client` is the tray and window: import a profile (Sallyport keeps a copy encrypted for your Windows
account), connect it — each profile is a SOCKS5 proxy on 127.0.0.1, on a free port taken when it connects — and launch apps through it: a browser
with a profile of its own, Firefox, an Electron app, or any tool that reads `ALL_PROXY`. Everything else on the machine
keeps its own connection. Games and programs that ignore proxy settings are not routed yet (`docs/design/architecture.md`).

```
Sallyport.Client.exe [profile.ovpn ...] [--tray] [--exit]
```

A profile path imports it; `--tray` starts out of sight; `--exit` asks the running client to disconnect everything and
quit. Building it needs Node.js for its page (`apps/Sallyport.Client/web`, built into the executable).

## Licence and provenance

MIT. **Written from the OpenVPN protocol's public description, not from OpenVPN's source code** — OpenVPN 2 is GPLv2 and
OpenVPN 3 is AGPLv3, and neither's code is used, ported or translated here (`docs/DECISIONS.md`, D1).

"OpenVPN" is a registered trademark of OpenVPN Inc. Sallyport is an independent, OpenVPN-*compatible* implementation
and is not affiliated with or endorsed by OpenVPN Inc.

## Building

```
dotnet build Sallyport.slnx
dotnet test Sallyport.slnx
dotnet run --project tools/Sallyport.Probe -- <profile.ovpn>     # phase 1: connect and print the server's PUSH_REPLY
dotnet run --project tools/Sallyport.Probe -c Release -- <profile.ovpn> --concurrent 64   # phase 3: 64 connections at once
```

A profile holds a private key: it is read from wherever the caller keeps it and never copied into this repository.
