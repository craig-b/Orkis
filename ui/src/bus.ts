import type { RunEvent } from "./types.js";

// A single in-page fan-out for the daemon-wide event stream. The app shell owns the
// one /v1/events connection (a second fetch to the identical URL gets coalesced by
// the browser and never delivers) and republishes here; views subscribe.
export const eventBus = new EventTarget();

export function publishRunEvent(event: RunEvent): void {
  eventBus.dispatchEvent(new CustomEvent("run-event", { detail: event }));
}

export function publishConnection(connected: boolean): void {
  eventBus.dispatchEvent(new CustomEvent("connection", { detail: connected }));
}

export function onRunEvent(handler: (event: RunEvent) => void): () => void {
  const listener = (e: Event) => handler((e as CustomEvent<RunEvent>).detail);
  eventBus.addEventListener("run-event", listener);
  return () => eventBus.removeEventListener("run-event", listener);
}

export function onConnection(handler: (connected: boolean) => void): () => void {
  const listener = (e: Event) => handler((e as CustomEvent<boolean>).detail);
  eventBus.addEventListener("connection", listener);
  return () => eventBus.removeEventListener("connection", listener);
}
