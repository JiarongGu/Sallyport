import { getBridge } from '@shenora/react';
import { MODULE, type Commands } from './contract';

let focusOrigin: HTMLElement | null = null;
let focusKey: string | undefined;

export function getFocusOrigin(): HTMLElement | null {
  if (focusOrigin?.isConnected) return focusOrigin;
  return Array.from(document.querySelectorAll<HTMLElement>('[data-focus-key]'))
    .find((element) => focusKey !== undefined && element.dataset.focusKey === focusKey) ?? null;
}

export function post<K extends keyof Commands>(command: K, payload: Commands[K]): void {
  // The host may disable the trigger in the same state that opens the editor or confirmation.
  if (command === 'BEGIN_PORT_EDIT' || command === 'BEGIN_APP_EDIT' || command === 'ADD_APP' || command === 'REQUEST_REMOVE_PROFILE') {
    focusOrigin = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    focusKey = focusOrigin?.dataset.focusKey;
  }
  getBridge().post(MODULE, command, { payload });
}
