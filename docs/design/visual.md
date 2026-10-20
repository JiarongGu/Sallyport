# The window's look — a frameless frame after OpenVPN Connect (2026-10-20)

**Why it exists.** The owner, 2026-10-20: *"we should also polish the ui/ux to make it more like a modern application"*;
of a first polish in Windows Settings' manner, *"i dont think this is a better ui/ux design rn? and you probably better
use frameless logic to optimize the looking and we propbably dont need the window max button and you can take example
of how the latest openvpn client looks"*; of a Connect-style layout, *"we can have a little difference since we have
more options"* and *"keep the current window looking and do not change to a portrit looking but taking the idea as
reference"*; then *"polish the left menu (currenly its too empty? and the top does not really match when its
collapsed), also the right click menu for the tray need a better ui"*, *"just make it simple … the tray menu we have
issue is the spacing and padding"*, *"the collapse toggle probbaly can be at the line of the menu as a button so that
when it collapse we still can see the icon (and I think we need a better icon design for this app)"*. It sets the look
of `window.md`'s structure; it changes no page, word or intent there.

**How it was made.** Codex (`local/codex/ui-polish-design.md`, `codex exec -s read-only`) and Claude Fable answered one
brief (`local/codex/ui-polish-brief.md`) with a polish in Windows Settings' manner; Claude kept from them the tokens'
form, the icons as inline strokes, the status dot drawn by CSS, editors as inset panels, notices as tinted bars, the
caption buttons dimmed with a dialog, and `Fact.Figure`, and set their look aside on the owner's word. OpenVPN Connect
served as the reference from its public release notes and tutorials only — 3.8 shows one profile with one switch; 3.7's
list of quick toggles is what its users asked back for — never from code of theirs (AGENTS.md, rule 1).

## The frame

- **Frameless, no maximize.** No title bar and no tab strip: a sidebar, and a page under a 48px header that is the title
  bar (the page's title, its actions, minimize and close). `MainForm` strips `WS_MAXIMIZEBOX` (Shenora's frameless frame
  keeps it whatever `MaximizeBox` says) and refuses `SC_MAXIMIZE`; the window stays resizable (`WS_THICKFRAME`).
- **The rail** (76 wide, always — the owner: *"let just make it always collapsed, and a status show under button
  (connected or not) … we dont really need the top connect seciont in the menu"*): the mark; Overview, Apps, Profiles,
  each an icon with its word under it; Settings at the foot; under the buttons whether anything is connected, a dot in
  its tone and the host's word for it. The connection's switch is the Overview's, not the menu's.
- **The splash** (`Splash.cs`) draws the rail's head (the mark, centred) at the page's geometry and a loading line where the content
  begins; the page lifts it (`CLOSE_SPLASH`) once its first state is painted.
- **A view inside a page** — an app's page, a profile's own connection settings — takes the page's place under a header
  with a back arrow (the chevron turned to point back) before its title. Back, or another page from the rail, leaves it
  and cancels an edit open there.

## The pages

- **Overview** (the Network page, renamed: *"the first page is more like the overview (main control for toggle)"*), where
  the window opens once a profile exists, shows one connection first, as Connect does: a profile picker, a large switch, its state in capitals with
  its time and proxy, received and sent as figures with the rate now over a minute's graph of both (`Connections`
  samples each channel once a second and publishes when a rate moves), its round trip as a figure in the graph's corner (the owner: *"in the chart (as a
  number not the line)"*), one line under it — UDP or TCP, its proxy, what is open through it, nothing folded (*"make
  the ui simple"*) — the routed app with Stop, the apps through it with Launch and a route switch; other open channels after.
- **Profiles**: a switch per profile, the address under the name with a copy icon once its proxy has a port ("a free
  port when it connects" in plain text until then), the details' chevron centred in its square; Connection settings…
  opens the profile's own view. **Apps**: one "Add an app…"; a tile, Launch, a route switch, the routing check, and an
  arrow (the chevron turned to point on) that opens the app's page — Monitoring once it has something to show, Programs
  (those it uses first, the others folded), Setup. Every app's switch has its word beside it, *Use VPN*. **Settings**: rows of
  what acts the moment it changes — General (Language, Start with Windows as a switch) and Connection (VPN protocol, If
  it cannot connect) — each a word, a line under it, and its control on the right.

## Tokens

Light and dark, every colour a token on `:root` (`styles.css`); `Theme.cs` carries the same values for what the form
draws. Light: base `#f5f7fa`, sidebar `#e9edf3`, cards white, text `#18202c`, secondary `#5a6575`, accent `#185fc7`,
connected `#137347` (its switch `#1f9d55`). Dark: base `#14181e`, sidebar `#0f1216`, cards `#1c2129`, text `#e8ecf2`,
secondary `#9aa5b4`, accent `#5b9dff`, connected `#3ccf8e`. Controls 32 high with 6px corners, cards 10, the connection
14. Contrast was computed for the pairs used as text (4.5:1) and control boundaries (3:1); not measured on the build.

## The mark

A gateway — a sallyport is a fortress's gate — with a way out rising through it, white strokes on a rounded tile in a
blue-to-indigo gradient (`#3d8bff` → `#5b4be0`), on a 32-unit grid: tile 1–31 with corners of 8; arch legs at 8.5 and
23.5 from 24 up to 15.5 under a half circle of radius 7.5; arrow from 24.5 to 15.5 with its head from 13 and 19 at 18.5;
strokes 2.75, round. `BrandMark.cs` draws it for the window, the taskbar, the tray (its tile in the state's colour) and
the splash; `Chrome.tsx` (`GateMark`) for the page; `icon.ps1` writes `Sallyport.ico` for Explorer and setup.

## The tray's menu

Kept simple, as before — Open Sallyport (which opens the Overview), each profile, the routed app or the Apps, Settings, Exit —
spaced as Windows 11's own menus: entries 32 high, the text 16 from the edge and centred on the entry, the submenu
chevron 16 from the other edge with 24 before it, separators 9 high, a rounded selection inset 4, rounded corners on
Windows 11. The renderer places the text and sizes each menu (`StatusTray.Fit`): WinForms would size the window and its
entries by two different measures of where it puts the text itself.
