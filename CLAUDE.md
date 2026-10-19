# CLAUDE.md — Sallyport

An app-scoped, OpenVPN-compatible client in pure .NET (README). These rules are binding.

1. **Clean-room. Never read, port or translate OpenVPN's source** (OpenVPN 2 is GPLv2, OpenVPN 3 is AGPLv3; this is MIT).
   Work from the protocol's public description, the server's own behaviour, and packet captures. If a detail is only
   learnable from their code, it is measured against a real server instead — and the measurement is written down.
2. **No secret ever enters the repository.** A profile (`.ovpn`) holds a private key and a static key: it is read from
   where its owner keeps it, never copied here, never logged, never printed — not even in a test failure. Tests use keys
   they generate.
3. **Every phase is measured against a real server before it is called done**, and the measurement goes in TASKS/the
   commit: what was sent, what came back. A unit test proves the code agrees with itself; the server proves it agrees
   with OpenVPN.
4. **No runtime dependencies beyond the .NET base library.** Cryptography and TLS are the BCL's (`AesGcm`, `Aes`,
   `HMACSHA256`, `SslStream`). Adding a package is a decision in `docs/DECISIONS.md`.
5. **Comments say why, never what.** Where the wire format is the reason, name the field and its size.
6. **Three outcomes where something is checked**: it holds, it failed, or it could not be checked — never collapsed.

Layout: `src/` holds the packages (`Sallyport`, the library), `apps/` the applications built on them
(`Sallyport.Host`, Kobo's provider), `tools/Sallyport.Probe` (a console that drives one phase against a real server),
`tests/Sallyport.Tests`. Decisions: `docs/DECISIONS.md`; designs: `docs/design/`. Work left: `TASKS.md` (open items only).
