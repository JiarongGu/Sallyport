// The window's contract with the host (apps/Sallyport.Client/ClientView.cs), from docs/design/client-ui.md §4.
// The host owns every decision and every word that depends on state: it pushes CLIENT.STATE whole, and CLIENT.NOTICE for
// each outcome. The page renders what it is sent and posts intents that answer nothing. It may format a duration and hold
// purely visual state (the page shown, a row expanded, text being typed) — nothing else.

export const MODULE = 'CLIENT';

export type Tone = 'off' | 'busy' | 'up' | 'trouble' | 'info';
export type AppKind = 'chromium' | 'firefox' | 'electron' | 'environment';

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
  /** Pressed through the matching top action (`actions.import` or `actions.addApp`), which says its label. */
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
  /** "127.0.0.1:1088" */
  endpointText: string;
  port: number;
  /** The details section: Transport, Imported, SOCKS5 port, Tunnel address, Open connections, Downloaded, Uploaded. */
  facts: Fact[];
  location: LocationView;
  actions: Record<'connect' | 'disconnect' | 'copy' | 'editPort' | 'locate' | 'remove', Action>;
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

export type EditorField = 'form' | 'portText' | 'name' | 'profileId' | 'kind' | 'arguments';

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
} & ({ kind: 'port'; values: PortValues } | { kind: 'app'; values: AppValues });

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
  apps: AppView[];
  profilesEmpty: Empty | null;
  appsEmpty: Empty | null;
  /** "Games and programs that ignore proxy settings are not routed yet." */
  appsNote: string;
  profileChoices: { id: string; label: string; enabled: boolean }[];
  /** `help` says what choosing that kind does, for the editor to show beside the choice being made. */
  kindChoices: { value: AppKind; label: string; help: string }[];
  editor: Editor | null;
  confirmation: Confirmation | null;
  guide: { endpointText: string; curlExample: string; copyExample: Action };
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
  CONFIRM_REMOVE_PROFILE: { token: string };
  CANCEL_CONFIRMATION: { token: string };
  COPY_GUIDE_EXAMPLE: Record<string, never>;
  DISMISS_NOTICE: { id: string };
}
