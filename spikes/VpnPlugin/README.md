# VPN plug-in spike (D12, D13)

Throwaway code that measured whether Sallyport can route one program through Windows' own per-app VPN, as a VPN
plug-in. The results are in `docs/DECISIONS.md` (D12); this folder is their record, not a product.

- `Plugin/` — the plug-in: a C#/WinRT component the platform activates as the `vpnClient` task. It logs each packet it
  is given and drops it; there is no tunnel inside it.
- `PluginHost/` — the task's custom host: a .NET executable running `CoreApplication`.
- `Control/` — a console run through the package's execution alias: adds the profile and connects it **under the guard**
  (D13), prints the plug-in's log, and `panic`s.
- `UdpSend/` — copied into a "routed" and a "direct" folder, so only the app filter tells the two apart.
- `build.ps1` — publishes, packs with `makeappx` and signs with a test certificate. The tools (Microsoft.Windows.SDK.BuildTools)
  and the certificate live outside the repository.

**Safety, not optional (D13).** The first run took the machine's network down. Connect only with
`sallyport-vpnspike connect <name> --for <s>`, which watches the direct path from outside the VPN and tears the
connection down when it fails. If anything looks wrong: `sallyport-vpnspike panic`.
