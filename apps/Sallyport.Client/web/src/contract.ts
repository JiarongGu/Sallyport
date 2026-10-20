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
  /** The details section: Transport, Imported, SOCKS5 port, Tunnel address, Open connections, Downloaded, Uploaded. */
  facts: Fact[];
  location: LocationView;
  actions: Record<'connect' | 'disconnect' | 'copy' | 'locate' | 'remove' | 'settings', Action>;
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

/** An app's routing, as the channel it goes through shows it. */
export interface SelectionView {
  id: string;
  name: string;
  run: RunView;
  /** Posts STOP_RUN. */
  stop: Action;
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
  facts: Fact[];
  /** "SOCKS5 proxy on 127.0.0.1:1088", when the channel serves one. */
  proxyText: string | null;
  selections: SelectionView[];
  /** Disconnect posts DISCONNECT with the channel's profileId; checkLatency posts CHECK_LATENCY with it. */
  actions: { disconnect: Action; checkLatency: Action };
  /** ISO time the next round of attempts starts, while the channel waits for it. */
  nextAttemptAt: string | null;
  /** Round trips, when the channel is up or was checked. */
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
  statusText: string;
  /** Each path and its result: "Direct to 1.1.1.1" — figure "14 ms", value "4 of 4 answered"; or no figure and
   *  "No reply — latency unavailable". */
  paths: Fact[];
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
}

export interface SettingsView {
  /** The packet driver and the Sallyport service, with install and remove. */
  routing: DriverView;
  /** Toggle posts SET_START_WITH_WINDOWS with the opposite of `on`. */
  startup: { on: boolean; label: string; text: string; toggle: Action };
  guide: { endpointText: string; curlExample: string; copyExample: Action };
  /** Version, where it is installed, where its data is. */
  about: Fact[];
  /** What a new app starts with; edit posts BEGIN_DEFAULTS_EDIT. */
  defaults: { facts: Fact[]; edit: Action };
  /** The language in force ("en", "zh-Hans") and the choices; choosing posts SET_LANGUAGE. */
  language: { setting: string; choices: { id: string; label: string }[] };
}

/** What a new app starts with: a profile (null: the first that can connect) and its loss policy. */
export interface DefaultsValues {
  profileId: string | null;
  loss: LossMode;
  directAfterText: string;
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
  /** toggle posts SET_APP_PROGRAM with the opposite of `included`; edit posts BEGIN_PROGRAM_EDIT; launch posts LAUNCH_APP with its id. */
  actions: Record<'toggle' | 'edit' | 'launch', Action>;
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
  /** Its routing, now or the last one; null when it has not been routed since the client started. */
  run: RunView | null;
  /** "3 programs included of 7."; empty for a lone program. */
  programsText: string;
  programs: ProgramView[];
  actions: Record<'route' | 'stop' | 'launch' | 'edit' | 'rescan' | 'addProgram' | 'remove', Action>;
  /** The profile it goes through, when one is chosen: the Network page lists an app under its profile. */
  profileId: string | null;
}

export interface AppEditValues {
  name: string;
  profileId: string | null;
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
}

export type EditorField = 'form' | 'name' | 'profileId' | 'kind' | 'arguments' | 'loss' | 'directAfterText' | 'launchProgramId'
  | 'retryWaitText' | 'retryWaitMaxText' | 'roundsMaxText' | 'attemptTimeoutText' | 'pingRestartText';

/** An edit the host opened. Its values seed the fields once (keep what the person types across later STATEs with the
 *  same token); its errors are the host's verdict on the last save. */
export type Editor = {
  token: string;
  subjectId: string;
  phase: 'editing' | 'saving';
  errors: Partial<Record<EditorField, string>>;
  /** Guidance under the fields: what the profile itself says. */
  hint: string | null;
  save: Action;
  cancel: Action;
  /** The policy editor's "Use the profile's own": posts RESET_POLICY. */
  reset: Action;
  /** The app editor's choices for what Launch starts. */
  options: { id: string; label: string }[] | null;
} & ({ kind: 'app'; values: AppEditValues } | { kind: 'program'; values: ProgramEditValues }
  | { kind: 'policy'; values: PolicyValues } | { kind: 'defaults'; values: DefaultsValues });

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
  /** The tab a first look opens on: Profiles while there is nothing to route through, Apps after. */
  initialPage: Page;
  /** A request from the host (the tray's Open Network) to show a page: followed once per new `request`. */
  navigation: { page: Page; request: number } | null;
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
}

/** Every intent the page may post, with its payload. None is answered: what follows arrives as the next STATE. */
export interface Commands {
  IMPORT: Record<string, never>;
  CONNECT: { id: string };
  DISCONNECT: { id: string };
  LOCATE: { id: string };
  CHECK_LATENCY: { id: string };
  BEGIN_DEFAULTS_EDIT: Record<string, never>;
  SAVE_DEFAULTS: { token: string; values: DefaultsValues };
  COPY_ENDPOINT: { id: string };
  /** One way in: the app's program, a game's folder made of it (ClientCore.AddAppFromDialogAsync). */
  ADD_APP: Record<string, never>;
  ADD_APP_PROGRAM: { id: string };
  RESCAN_APP: { id: string };
  SET_APP_PROGRAM: { id: string; programId: string; included: boolean };
  BEGIN_APP_EDIT: { id: string };
  SAVE_APP: { token: string; values: AppEditValues };
  BEGIN_PROGRAM_EDIT: { id: string; programId: string };
  SAVE_PROGRAM: { token: string; values: ProgramEditValues };
  REQUEST_REMOVE_APP: { id: string };
  ROUTE_APP: { id: string };
  CANCEL_EDIT: { token: string };
  /** Without a programId: what the app's Launch starts. */
  LAUNCH_APP: { id: string; programId?: string };
  REQUEST_REMOVE_PROFILE: { id: string };
  /** Whatever the open confirmation asks: removing a profile or an app, disconnecting, installing or removing the driver. */
  CONFIRM: { token: string };
  STOP_RUN: Record<string, never>;
  BEGIN_POLICY_EDIT: { id: string };
  SAVE_POLICY: { token: string; values: PolicyValues };
  RESET_POLICY: { token: string };
  REQUEST_DRIVER_INSTALL: Record<string, never>;
  REQUEST_DRIVER_REMOVE: Record<string, never>;
  CANCEL_CONFIRMATION: { token: string };
  COPY_GUIDE_EXAMPLE: Record<string, never>;
  SET_START_WITH_WINDOWS: { enabled: boolean };
  SET_LANGUAGE: { language: string };
  DISMISS_NOTICE: { id: string };
}
