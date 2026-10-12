# TASKS.md — open work only

A finished item is deleted (git is the archive); a phase is done when it is measured against a real server.

## Phase 3 — what is left
- [ ] Many concurrent connections — a browser page opens dozens. Only one or two at a time have been measured.

Kobo adopted it on 2026-10-12, as a provider process (`Sallyport.Host.exe serve|stop`). Its browser rendered a FANZA
product page through Sallyport with WSL's OpenVPN stopped. In-process hosting waits for the release.

## Phase 4 — the long run
Reconnect, fallback across a profile's remotes, renegotiation over hours (`ControlStream` refuses a soft reset today —
the server's default is hourly), status and byte counts as events for a host (Kobo's Network page), `auth-user-pass`,
`tls-auth`, `tls-crypt-v2` and `P_DATA_V1` profiles. Uploads at volume (the send side has no SACK of its own). Then release.

**Written but never run against a server:** the TCP transport (the test profile's second remote is `tcp/443`); the
keepalive over minutes; whether the server acts on the exit notices (`explicit-exit-notify`, `EXIT`).
