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
}

export interface Empty {
  title: string;
  text: string;
  /** Pressed through the matching top action (`actions.import`, `actions.addApp`, `actions.addGame`), which says its label. */
  command: 'IMPORT' | 'ADD_APP' | 'ADD_GAME';
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
  /** "127.0.0.1:1088" */
  endpointText: string;
  port: number;
  /** The details section: Transport, Imported, SOCKS5 port, Tunnel address, Open connections, Downloaded, Uploaded. */
  facts: Fact[];
  location: LocationView;
  actions: Record<'connect' | 'disconnect' | 'copy' | 'editPort' | 'locate' | 'remove' | 'settings', Action>;
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

export interface GameProgramView {
  path: string;
  name: string;
  included: boolean;
  /** Why it is left out, when it is: "installs or removes software". */
  reason: string | null;
  /** Posts SET_GAME_PROGRAM with the opposite of `included`. */
  toggle: Action;
}

export interface GameView {
  id: string;
  name: string;
  folder: string | null;
  /** "Through jp" */
  throughText: string;
  /** "If the VPN drops: the game's traffic is held" */
  lossText: string;
  tone: Tone;
  statusText: string;
  /** "3 programs routed of 7." */
  programsText: string;
  programs: GameProgramView[];
  /** The run of this game, now or the last one; null when it has not run since the client started. */
  run: RunView | null;
  actions: Record<'run' | 'stop' | 'edit' | 'rescan' | 'remove', Action>;
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
  /** Disconnect posts DISCONNECT with the channel's profileId. */
  actions: { disconnect: Action };
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
  /** Version, where it is installed, where its data is, the driver and the service. */
  about: Fact[];
}

export interface AppView {
  id: string;
  name: string;
  kind: AppKind;
  kindLabel: string;
  kindHelp: string;
  path: string;
  profileId: string | null;
  /** "Through jp" */
  throughText: string;
  phase: 'ready' | 'blocked' | 'connecting' | 'starting';
  /** Why it is blocked, or what it is doing: "App not found", "Connecting jp before launch…" */
  statusText: string | null;
  /** The flags, preferences or environment it starts with, one per line. */
  startupSettings: string[];
  actions: Record<'launch' | 'edit' | 'remove', Action>;
}

export interface PortValues {
  portText: string;
}

export interface AppValues {
  name: string;
  profileId: string | null;
  kind: AppKind;
  arguments: string;
}

export interface GameValues {
  name: string;
  profileId: string | null;
  loss: LossMode;
  /** Under 'hold': seconds before the game goes direct after all, or empty to hold until the VPN is back. */
  directAfterText: string;
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

export type EditorField = 'form' | 'portText' | 'name' | 'profileId' | 'kind' | 'arguments' | 'loss' | 'directAfterText'
  | 'retryWaitText' | 'retryWaitMaxText' | 'roundsMaxText' | 'attemptTimeoutText' | 'pingRestartText';

/** An edit the host opened. Its values seed the fields once (keep what the person types across later STATEs with the
 *  same token); its errors are the host's verdict on the last save. */
export type Editor = {
  token: string;
  subjectId: string;
  phase: 'editing' | 'saving';
  errors: Partial<Record<EditorField, string>>;
  /** Guidance under the fields: "Use a port from 1024 to 65535." */
  hint: string | null;
  save: Action;
  cancel: Action;
  /** The policy editor's "Use the profile's own": posts RESET_POLICY. */
  reset: Action;
} & ({ kind: 'port'; values: PortValues } | { kind: 'app'; values: AppValues } | { kind: 'game'; values: GameValues }
  | { kind: 'policy'; values: PolicyValues });

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
  subject: { kind: 'profile' | 'app' | 'game'; id: string } | null;
  createdAt: string;
  /** Posts DISMISS_NOTICE with this notice's id. */
  dismiss: Action;
}

export interface ClientState {
  /** Newer replaces older; an older one arriving late is ignored. */
  sequence: number;
  /** The title bar's summary: "1 connected · 1 reconnecting", "Not connected". */
  summary: { text: string; tone: Tone };
  actions: { import: Action; addApp: Action; addGame: Action };
  profiles: ProfileView[];
  apps: AppView[];
  games: GameView[];
  profilesEmpty: Empty | null;
  appsEmpty: Empty | null;
  gamesEmpty: Empty | null;
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
  /** Newest first. Info notices leave on their own after a few seconds; warnings and errors stay until dismissed. */
  notices: Notice[];
}

/** Every intent the page may post, with its payload. None is answered: what follows arrives as the next STATE. */
export interface Commands {
  IMPORT: Record<string, never>;
  CONNECT: { id: string };
  DISCONNECT: { id: string };
  LOCATE: { id: string };
  COPY_ENDPOINT: { id: string };
  BEGIN_PORT_EDIT: { id: string };
  SET_PORT: { token: string; portText: string };
  ADD_APP: Record<string, never>;
  BEGIN_APP_EDIT: { id: string };
  UPDATE_APP: { token: string; values: AppValues };
  CANCEL_EDIT: { token: string };
  LAUNCH_APP: { id: string };
  REMOVE_APP: { id: string };
  REQUEST_REMOVE_PROFILE: { id: string };
  /** Whatever the open confirmation asks: removing a profile or a game, installing or removing the driver. */
  CONFIRM: { token: string };
  ADD_GAME: Record<string, never>;
  RESCAN_GAME: { id: string };
  SET_GAME_PROGRAM: { id: string; path: string; included: boolean };
  BEGIN_GAME_EDIT: { id: string };
  SAVE_GAME: { token: string; values: GameValues };
  REQUEST_REMOVE_GAME: { id: string };
  RUN_GAME: { id: string };
  STOP_RUN: Record<string, never>;
  BEGIN_POLICY_EDIT: { id: string };
  SAVE_POLICY: { token: string; values: PolicyValues };
  RESET_POLICY: { token: string };
  REQUEST_DRIVER_INSTALL: Record<string, never>;
  REQUEST_DRIVER_REMOVE: Record<string, never>;
  CANCEL_CONFIRMATION: { token: string };
  COPY_GUIDE_EXAMPLE: Record<string, never>;
  SET_START_WITH_WINDOWS: { enabled: boolean };
  DISMISS_NOTICE: { id: string };
}
