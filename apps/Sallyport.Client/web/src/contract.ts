// The window's contract with the host (apps/Sallyport.Client/ClientView.cs), from docs/design/client-ui.md §4.
// The host owns every decision and every word that depends on state: it pushes CLIENT.STATE whole, and CLIENT.NOTICE for
// each outcome. The page renders what it is sent and posts intents that answer nothing. It may format a duration and hold
// purely visual state (the page shown, a row expanded, text being typed) — nothing else.

export const MODULE = 'CLIENT';

/** The window's four tabs (docs/design/window.md). */
export type Page = 'apps' | 'network' | 'profiles' | 'settings';

export type Tone = 'off' | 'busy' | 'up' | 'trouble' | 'info';
export type AppKind = 'chromium' | 'firefox' | 'electron' | 'environment';
/** What a game's traffic does while its VPN is lost (docs/design/product.md §3). */
export type LossMode = 'hold' | 'direct' | 'stop';

/** A button as the host decides it: whether it shows, whether it may be pressed, and why not. */
export interface Action {
  visible: boolean;
  enabled: boolean;
  /** The work it starts is under way: show it busy, and disabled. */
  busy: boolean;
  label: string;
  /** Why it is disabled, when it is; shown beside it or as its description. */
  reason: string | null;
}

export interface Fact {
  label: string;
  value: string;
  /** The part shown large, when there is one (a round trip's "19 ms"); `value` then qualifies it. */
  figure?: string | null;
}

export interface Empty {
  title: string;
  text: string;
  /** Pressed through the matching top action (`actions.import`, `actions.addApp`), which says its label. */
  command: 'IMPORT' | 'ADD_APP';
}

export type ProfilePhase =
  | 'idle'
  | 'connecting'
  | 'up'
  | 'reconnecting'
  | 'disconnecting'
  | 'stopped'
  | 'refused'
  | 'unsupported'
  | 'removing';

export interface LocationView {
  phase: 'unchecked' | 'checking' | 'checked' | 'unavailable' | 'disconnected';
  /** The line to show: "Exit location: Tokyo, Tokyo, JP", "Checking location…", "Connect to check location." */
  text: string;
  /** IP, organisation — whatever the lookup returned. */
  facts: Fact[];
  /** ISO time of the result shown, when there is one. */
  checkedAt: string | null;
  /** A result kept on screen while it is checked again: label it "Previous result". */
  previousResult: boolean;
}

export interface ProfileView {
  id: string;
  name: string;
  phase: ProfilePhase;
  tone: Tone;
  /** "Connected", "Reconnecting…", "Could not connect" */
  statusText: string;
  /** The supporting line under it, when there is one: a reason, an attempt count. */
  detailText: string | null;
  /** ISO time the tunnel came up; the page shows "Connected for {duration}" from it, ticking. */
  connectedSince: string | null;
  /** "127.0.0.1:52114" while its proxy listens; otherwise that it takes a free port when it connects. */
  endpointText: string;
  /** The port its proxy listens on, chosen when it connects; null while it does not. */
  port: number | null;
  /** Its row on Profiles: VPN protocol, If it cannot connect, More settings (each Settings' or its own), Imported. */
  details: Fact[] | null;
  location: LocationView;
  actions: Record<'connect' | 'disconnect' | 'copy' | 'locate' | 'remove' | 'edit', Action>;
  /** ISO time the next round of attempts starts, while the connection waits for it: the page counts down to it. */
  nextAttemptAt: string | null;
}

/** Three outcomes, never two (rule 6). */
export interface CheckView {
  result: 'holds' | 'failed' | 'unavailable';
  text: string;
}

/** A game's run, as the router service and the tunnel report it. */
export interface RunView {
  phase: 'preparing' | 'connecting' | 'routing' | 'held' | 'direct' | 'ended';
  tone: Tone;
  /** "Routed through jp", "Reconnecting — the game's traffic is held", "Routing stopped" */
  statusText: string;
  detailText: string | null;
  /** ISO time routing began, while routing; the page shows "Routed for {duration}". */
  since: string | null;
  facts: Fact[];
  /** Whether everything of the game went through the VPN. */
  coverage: CheckView;
  /** What the app has had through the VPN on this route, for the Overview: "1.2 MB" each way, its round trip "45 ms". */
  usage: { received: string; sent: string; latency: string | null } | null;
}

export interface DriverView {
  phase: 'ready' | 'notInstalled' | 'serviceMissing' | 'working' | 'unknown';
  tone: Tone;
  statusText: string;
  detailText: string | null;
  facts: Fact[];
  install: Action;
  remove: Action;
}

/** An app in one profile's section (D24): on the Overview, the app (its name and icon); on the app's page, the profile.
 *  vpn posts SET_APP_VPN with the opposite of `on`; launch posts LAUNCH_APP with this profile; remove posts
 *  REMOVE_APP_FROM_PROFILE. */
export interface AppSectionView {
  appId: string;
  profileId: string;
  name: string;
  iconUrl: string | null;
  /** In the section at all; on its page, a profile it is not in reads as not used. */
  member: boolean;
  on: boolean;
  tone: Tone;
  statusText: string;
  detailText: string | null;
  /** Its part of the route while this profile carries it. */
  run: RunView | null;
  vpn: Action;
  launch: Action;
  remove: Action;
}

/** A profile's section on the Overview: the profile and its switch, its connection while it has one, its apps, and the
 *  apps that could join it (add posts ADD_APP_TO_PROFILE with one of `addable`). */
export interface SectionView {
  profile: ProfileView;
  channel: ChannelView | null;
  apps: AppSectionView[];
  addable: { id: string; label: string; help: string | null }[];
  add: Action;
  /** What to do when it has no app yet. */
  hintText: string | null;
}

/** One profile's connection: what it is, what goes through it (its proxy, the apps routed), and how to end it. */
export interface ChannelView {
  id: string;
  profileId: string;
  name: string;
  tone: Tone;
  statusText: string;
  detailText: string | null;
  /** ISO time the tunnel came up; the page shows "Connected for {duration}" from it. */
  connectedSince: string | null;
  /** One line, its parts joined: "UDP", "SOCKS5 proxy on 127.0.0.1:1088", "2 open connections", "Reconnected once". */
  line: string[];
  /** Disconnect posts DISCONNECT with the channel's profileId. Its latency is checked on its own while it is up. */
  actions: { disconnect: Action };
  /** ISO time the next round of attempts starts, while the channel waits for it. */
  nextAttemptAt: string | null;
  /** Its round trip on the traffic graph, when the channel is up or was checked. */
  latency: LatencyView | null;
  /** What it has received and sent, and the rates over the last minute, while it is open. */
  traffic: TrafficView | null;
}

export interface TrafficView {
  /** Figure "1.2 MB", value "12 KB/s" — the rate now. */
  received: Fact;
  sent: Fact;
  /** Bytes per second, a second apart, oldest first: the graph's two lines. */
  inPerSecond: number[];
  outPerSecond: number[];
}

export interface LatencyView {
  phase: 'unchecked' | 'checking' | 'available' | 'unavailable';
  /** "48 ms": the VPN's round trip; null when there is none yet or none answered. */
  figure: string | null;
  /** What the figure is ("Through the VPN to 1.1.1.1"), or why there is none. */
  text: string;
  checkedAt: string | null;
}

export interface NetworkView {
  /** "2 connections · 1 app routed", "Nothing is connected" */
  statusText: string;
  tone: Tone;
  /** Shown when nothing is connected, with links to Apps and Profiles. */
  empty: { title: string; text: string } | null;
  /** Trouble first. */
  channels: ChannelView[];
  /** The packet driver and the Sallyport service, as they are. */
  routing: DriverView;
  /** Every profile that can connect, each with the apps in it (D24). */
  sections: SectionView[];
}

export interface SettingsView {
  /** The packet driver and the Sallyport service, with install and remove. */
  routing: DriverView;
  /** Toggle posts SET_START_WITH_WINDOWS with the opposite of `on`. */
  startup: { on: boolean; label: string; text: string; toggle: Action };
  guide: { endpointText: string; curlExample: string; copyExample: Action };
  /** Its version. */
  about: Fact[];
  /** Where it is installed and where its data is: long paths, folded under Technical details. */
  places: Fact[] | null;
  /** The language in force ("en", "zh-Hans") and the choices; choosing posts SET_LANGUAGE. */
  language: { setting: string; choices: { id: string; label: string }[] };
  /** How every profile connects, unless changed for one in its own view. Choosing posts SET_PROTOCOL ('', 'udp', 'tcp')
   *  or SET_RETRY (failed rounds before it stops, 0 never); `help` says what the choice in force does. */
  connection: {
    protocol: { setting: string; choices: { id: string; label: string; help: string | null }[] };
    retry: { setting: string; choices: { id: string; label: string; help: string | null }[] };
  };
  /** Export posts BEGIN_EXPORT, Import BEGIN_IMPORT (D26). */
  move: { export: Action; import: Action } | null;
  /** About's Uninstall Sallyport…: setup's removal, for an installed copy only. Posts UNINSTALL. */
  uninstall: Action | null;
  /** A newer Sallyport (D34), for an installed copy only: check posts CHECK_UPDATE, apply APPLY_UPDATE. */
  update: UpdateView | null;
}

/** What is known of a newer Sallyport and what can be done; done, how far a download is (0 to 1). */
export interface UpdateView {
  phase: 'unchecked' | 'checking' | 'upToDate' | 'available' | 'downloading' | 'starting' | 'failed' | 'unavailable';
  statusText: string;
  check: Action;
  apply: Action;
  done: number | null;
}

/** One program of an app, as its row shows it. */
export interface ProgramView {
  id: string;
  /** The file's name: "ZenlessZoneZero.exe" */
  name: string;
  path: string;
  included: boolean;
  /** Its methods — "All traffic", "Launched with proxy settings (Chromium browser)" — or why it is left out. */
  statusText: string;
  /** Not where it was added from any more. */
  missing: boolean;
  /** The one Launch starts. */
  launchTarget: boolean;
  /** The flags, preferences or environment a proxy launch gives it, one per line; empty without one. */
  startupSettings: string[];
  /** toggle posts SET_APP_PROGRAM with the opposite of `included`; edit posts BEGIN_PROGRAM_EDIT; launch posts LAUNCH_APP with its id;
   *  icon posts SET_APP_ICON with its id. */
  actions: Record<'toggle' | 'edit' | 'launch' | 'icon', Action>;
  /** The folder it sits in under its app's, when not at the top ("APMCrashReporter"): two of one name tell apart. */
  folder: string | null;
  /** What it is doing now — "Running · through jp", "Not running" — while its app's page is open (WATCH_APP); else null. */
  activity: { tone: Tone; text: string } | null;
  /** Its details: how it is routed, its connections now, what went through the VPN, its file. */
  facts: Fact[] | null;
}

/** An app — a program, or a folder of them — and its routing (docs/design/window.md). */
export interface AppItemView {
  id: string;
  name: string;
  folder: string | null;
  /** "Through jp" */
  throughText: string;
  /** "All traffic of 3 programs · 1 launched with proxy settings" */
  methodText: string;
  /** "If the VPN drops: …", when it routes packets. */
  lossText: string | null;
  tone: Tone;
  statusText: string;
  /** "Launch starts ZenlessZoneZero.exe" */
  launchText: string | null;
  /** Its part of the route while its profile carries it; null otherwise. */
  run: RunView | null;
  /** "3 programs included of 7."; empty for a lone program. */
  programsText: string;
  programs: ProgramView[];
  /** locate: Locate…, for an app not where it was added from — posts LOCATE_APP. */
  actions: Record<'launch' | 'edit' | 'rescan' | 'addProgram' | 'remove', Action> & { locate: Action | null };
  /** Its VPNs on its page: every profile, those it is in first, each with its own switch (D24). */
  vpns: AppSectionView[] | null;
  /** What to do for it to go through the VPN, when it is on and does not yet: "Connect jp and it goes through the VPN." */
  detailText: string | null;
  /** Its program's own icon as a data URL; null shows its initial. */
  iconUrl: string | null;
  /** The profile it goes through, when one is chosen: the Network page lists an app under its profile. */
  profileId: string | null;
  /** Its page's setup, label by label: VPN profile, Routes, If the VPN drops, Folder, Launch starts. */
  setup: Fact[];
}

export interface AppEditValues {
  name: string;
  /** The program whose icon is the app's: one of the editor's `icons`. */
  iconProgramId: string | null;
  loss: LossMode;
  /** Under 'hold': seconds before the traffic goes direct after all, or empty to hold until the VPN is back. */
  directAfterText: string;
  /** What Launch starts: one of the editor's `options`. */
  launchProgramId: string | null;
}

/** A program's methods: all its traffic by the packet driver, launched with proxy settings, or both. */
export interface ProgramEditValues {
  packets: boolean;
  proxy: boolean;
  kind: AppKind;
  arguments: string;
}

/** A profile's connection settings, as OpenVPN's directives name them; seeded with what is in force. */
export interface PolicyValues {
  retryWaitText: string;
  retryWaitMaxText: string;
  /** 0: never give up. */
  roundsMaxText: string;
  randomOrder: boolean;
  attemptTimeoutText: string;
  retryAfterAuthFailure: boolean;
  /** Empty: the server's own. */
  pingRestartText: string;
  /** '': every server as the profile lists them; 'udp' or 'tcp': only those (the editor's options). */
  protocol: string;
  /** On: never give up. Off: stop after roundsMaxText failed rounds. */
  keepTrying: boolean;
}

/** "Add an app": where it is, then its name, profile, loss policy and which of its programs go through the VPN. */
export interface NewAppValues {
  path: string;
  name: string;
  /** The profiles it goes through: in each one's section, its Use VPN on (D24). */
  profileIds: string[];
  loss: LossMode;
  directAfterText: string;
  /** Paths of the programs (from the editor's options) that go through the VPN. */
  included: string[];
  /** The program, by path, whose icon the app takes: one of the editor's `icons`. */
  iconPath: string | null;
}

export type EditorField = 'form' | 'path' | 'name' | 'profileId' | 'kind' | 'arguments' | 'loss' | 'directAfterText' | 'launchProgramId'
  | 'retryWaitText' | 'retryWaitMaxText' | 'roundsMaxText' | 'attemptTimeoutText' | 'pingRestartText' | 'protocol' | 'password' | 'confirm'
  | 'link' | 'username';

/** "Import a VPN profile" (D29), as OpenVPN Connect's Import Profile: from a file or a link, then looked at before it is
 *  kept. signIn: the link's server asked who the person is, so a username and password are asked for. The host never
 *  sends a typed password back. */
export interface NewProfileValues {
  source: 'file' | 'link';
  link: string;
  username: string;
  password: string;
  name: string;
  connect: boolean;
  signIn: boolean;
  /** Keep the VPN password, sealed, for the next connect. */
  remember: boolean;
  /** The host's: the profile read signs in with a username and password. */
  needsLogin: boolean;
}

/** Edit a profile (D29): its name, for one that signs in its username and kept password, and how it connects —
 *  ownSettings off follows Settings, on keeps policy. needsLogin and passwordSaved are the host's; a kept password is
 *  never sent to the page. No policy for a profile this version cannot connect. */
export interface ProfileEditValues {
  name: string;
  username: string;
  password: string;
  changePassword: boolean;
  forgetPassword: boolean;
  needsLogin: boolean;
  passwordSaved: boolean;
  ownSettings: boolean;
  policy: PolicyValues | null;
}

/** The sign-in a connect waits for (D29), over whatever page is shown. */
export interface SignInView {
  token: string;
  profileId: string;
  title: string;
  server: string;
  username: string;
  passwordSaved: boolean;
  error: string | null;
  connect: Action;
  cancel: Action;
}

/** Export and Import (D26): which parts, and the password the profiles are sealed with. The host never sends a typed
 *  password back: what the page holds is what was typed. */
export interface MoveValues {
  profiles: boolean;
  apps: boolean;
  connection: boolean;
  password: string;
  confirm: string;
  /** Whether a password is asked: always offered on export (and may be left empty), on import only for a sealed file. */
  sealed: boolean;
}

/** An edit the host opened. Its values seed the fields once (keep what the person types across later STATEs with the
 *  same token); its errors are the host's verdict on the last save. */
export type Editor = {
  token: string;
  subjectId: string;
  /** fetching: the import's link is being fetched. */
  phase: 'editing' | 'saving' | 'fetching';
  errors: Partial<Record<EditorField, string>>;
  /** Guidance under the fields: what the profile itself says. */
  hint: string | null;
  save: Action;
  cancel: Action;
  /** The app editor's choices for what Launch starts; a profile's VPN protocols on its Edit page, null when it has
   *  servers of one only. */
  options: { id: string; label: string; help: string | null }[] | null;
  /** For an app, or one being added: the icons it may take, each a program's own (a data URL), one per look; an id is
   *  a program's id, or, for one being added, its path. */
  icons: { id: string; label: string; url: string }[] | null;
  /** For a profile being imported, once read: its server and where it came from. */
  facts: Fact[] | null;
} & ({ kind: 'app'; values: AppEditValues } | { kind: 'new-profile'; values: NewProfileValues } | { kind: 'profile-edit'; values: ProfileEditValues } | { kind: 'program'; values: ProgramEditValues }
  | { kind: 'new-app'; values: NewAppValues }
  | { kind: 'export'; values: MoveValues } | { kind: 'import'; values: MoveValues });

export interface Confirmation {
  token: string;
  title: string;
  text: string;
  confirm: Action;
  cancel: Action;
}

export interface Notice {
  id: string;
  kind: 'info' | 'warning' | 'error';
  text: string;
  subject: { kind: 'profile' | 'app'; id: string } | null;
  createdAt: string;
  /** Posts DISMISS_NOTICE with this notice's id. */
  dismiss: Action;
}

export interface ClientState {
  /** Newer replaces older; an older one arriving late is ignored. */
  sequence: number;
  /** The title bar's summary: "1 connected · 1 reconnecting", "Not connected". */
  summary: { text: string; tone: Tone };
  actions: { import: Action; addApp: Action };
  profiles: ProfileView[];
  apps: AppItemView[];
  profilesEmpty: Empty | null;
  appsEmpty: Empty | null;
  network: NetworkView;
  settings: SettingsView;
  /** The tab a first look opens on: Profiles while there is nothing to route through, the Overview after. */
  initialPage: Page;
  /** A request from the host (the tray's Open Network) to show a page: followed once per new `request`. */
  /** `app`: the app whose own page Apps opens on (a new app, just added). */
  navigation: { page: Page; request: number; app: string | null } | null;
  profileChoices: { id: string; label: string; enabled: boolean }[];
  /** `help` says what choosing that kind does, for the editor to show beside the choice being made. */
  kindChoices: { value: AppKind; label: string; help: string }[];
  lossChoices: { value: LossMode; label: string; help: string }[];
  editor: Editor | null;
  confirmation: Confirmation | null;
  /** The language the host speaks in this state, for the page's own words: "en" or "zh-Hans". */
  language: string;
  /** Newest first. Info notices leave on their own after a few seconds; warnings and errors stay until dismissed. */
  notices: Notice[];
  signIn: SignInView | null;
  /** Windows' Text size, 1 to 2.25: the page's root size is multiplied by it. */
  textScale: number;
}

/** Every intent the page may post, with its payload. None is answered: what follows arrives as the next STATE. */
export interface Commands {
  /** Opens "Import a VPN profile" (a new-profile editor); the file or link is chosen there. */
  IMPORT: Record<string, never>;
  PROFILE_IMPORT_BROWSE: { token: string; values: NewProfileValues };
  /** Get the profile from values.link, with values.username and password once the server has asked for them. */
  PROFILE_IMPORT_LINK: { token: string; values: NewProfileValues };
  SAVE_PROFILE_IMPORT: { token: string; values: NewProfileValues };
  SIGN_IN: { token: string; values: { username: string; password: string; remember: boolean } };
  CANCEL_SIGN_IN: { token: string };
  BEGIN_PROFILE_EDIT: { id: string };
  SAVE_PROFILE_EDIT: { token: string; values: ProfileEditValues };
  CONNECT: { id: string };
  DISCONNECT: { id: string };
  LOCATE: { id: string };
  BEGIN_EXPORT: Record<string, never>;
  SAVE_EXPORT: { token: string; values: MoveValues };
  BEGIN_IMPORT: Record<string, never>;
  SAVE_IMPORT: { token: string; values: MoveValues };
  COPY_ENDPOINT: { id: string };
  /** Opens "Add an app" (a new-app editor); its place is chosen there. */
  /** profileId: opened from that profile's section on the Overview, and ticked. */
  ADD_APP: { profileId?: string };
  /** One dialog for a folder or a program; what it picks goes to NEW_APP_LOCATION. */
  NEW_APP_BROWSE: { token: string; values: NewAppValues };
  /** The place typed or pasted: the host looks at what is there and answers with a new token. */
  NEW_APP_LOCATION: { token: string; values: NewAppValues };
  SAVE_NEW_APP: { token: string; values: NewAppValues };
  ADD_APP_PROGRAM: { id: string };
  RESCAN_APP: { id: string };
  SET_APP_PROGRAM: { id: string; programId: string; included: boolean };
  BEGIN_APP_EDIT: { id: string };
  SAVE_APP: { token: string; values: AppEditValues };
  BEGIN_PROGRAM_EDIT: { id: string; programId: string };
  SAVE_PROGRAM: { token: string; values: ProgramEditValues };
  REQUEST_REMOVE_APP: { id: string };
  SET_APP_VPN: { id: string; profileId: string; on: boolean };
  ADD_APP_TO_PROFILE: { id: string; profileId: string };
  REMOVE_APP_FROM_PROFILE: { id: string; profileId: string };
  SET_APP_ICON: { id: string; programId: string };
  LOCATE_APP: { id: string };
  CANCEL_EDIT: { token: string };
  /** Without a programId: what the app's Launch starts. */
  LAUNCH_APP: { id: string; programId?: string; profileId?: string };
  REQUEST_REMOVE_PROFILE: { id: string };
  /** Whatever the open confirmation asks: removing a profile or an app, disconnecting, installing or removing the driver. */
  CONFIRM: { token: string };
  REQUEST_DRIVER_INSTALL: Record<string, never>;
  REQUEST_DRIVER_REMOVE: Record<string, never>;
  CANCEL_CONFIRMATION: { token: string };
  COPY_GUIDE_EXAMPLE: Record<string, never>;
  SET_START_WITH_WINDOWS: { enabled: boolean };
  SET_LANGUAGE: { language: string };
  UNINSTALL: Record<string, never>;
  CHECK_UPDATE: Record<string, never>;
  APPLY_UPDATE: Record<string, never>;
  SET_PROTOCOL: { protocol: string };
  /** The app whose page is open (its programs are looked at while it is), or null when none is. */
  WATCH_APP: { id: string | null };
  SET_RETRY: { rounds: number };
  DISMISS_NOTICE: { id: string };
}
