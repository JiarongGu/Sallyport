# Decisions — Sallyport

## D1 — Written from the protocol, not from OpenVPN's code; MIT (2026-10-12)

The owner wanted OpenVPN brought into their own software "but avoid the agpl license". OpenVPN 3 Core, the embeddable
library, is AGPLv3; OpenVPN 2 is GPLv2. A protocol is not copyrightable; an implementation is. So Sallyport implements
the OpenVPN wire protocol from its public description and the server's observable behaviour, and uses no OpenVPN code —
not linked, not ported, not translated structure. Licensed MIT, like the owner's other packages (Lyntai, Shenora). The
name avoids the OpenVPN trademark; the README says "OpenVPN-compatible" and states the non-affiliation.

## D2 — The tunnel ends in a SOCKS5 proxy, not a network adapter (2026-10-12)

The point is a VPN for ONE application. An adapter (TAP/wintun) puts the tunnel in the machine's routing table, needs
administrator rights, and takes every application's traffic unless routes are fought over. Ending the tunnel inside
the process — a userspace TCP/IP stack behind a loopback SOCKS5 server — routes exactly what is pointed at it, with no
privileges. Kobo's browser route already consumes a SOCKS5 proxy, so the swap is invisible to it.

## D3 — Data-channel keys by the legacy PRF expansion, not the TLS exporter (2026-10-12)

Newer servers prefer deriving data keys with the TLS keying-material exporter (RFC 5705, "tls-ekm"). .NET's `SslStream`
does not expose an exporter. A client that does not advertise the exporter capability in its peer info is given the
older key-method-2 expansion (the TLS 1.0 PRF over both sides' random material), which servers still support. Revisit
if .NET gains an exporter, or if a server refuses clients without it.
