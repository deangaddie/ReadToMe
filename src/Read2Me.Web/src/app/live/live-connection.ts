import { InjectionToken } from '@angular/core';
import {
  HubConnectionBuilder,
  HubConnectionState,
  IRetryPolicy,
  LogLevel,
  RetryContext,
} from '@microsoft/signalr';

/** Root-relative so it reaches the host through the dev proxy and from under `/app/`. */
export const LIVE_HUB_URL = '/hubs/live';

/**
 * Backoff for both the initial connect loop and SignalR's automatic reconnect (ticket 07):
 * immediately, then 2 s, 5 s, 10 s, and 30 s forever after. Never gives up — a local host that
 * is being restarted comes back, and the app is useless without live updates anyway.
 */
export const RECONNECT_DELAYS_MS: readonly number[] = [0, 2000, 5000, 10000, 30000];

export function reconnectDelayMs(previousRetryCount: number): number {
  const index = Math.min(Math.max(previousRetryCount, 0), RECONNECT_DELAYS_MS.length - 1);
  return RECONNECT_DELAYS_MS[index] ?? 0;
}

/**
 * The slice of `HubConnection` the service uses, so tests can substitute a fake without the real
 * transport. `HubConnection` satisfies it structurally.
 */
export interface LiveConnection {
  readonly state: HubConnectionState;
  /** Null until connected; changes on every reconnect, so read it per request, never cache it. */
  readonly connectionId: string | null;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T = void>(methodName: string, ...args: unknown[]): Promise<T>;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any -- HubConnection's own signature
  on(methodName: string, newMethod: (...args: any[]) => void): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
}

export interface LiveConnectionOptions {
  url: string;
  /** Called before every automatic reconnect attempt with the attempt number (1-based). */
  onRetry: (attempt: number) => void;
}

export type LiveConnectionFactory = (options: LiveConnectionOptions) => LiveConnection;

function retryPolicy(onRetry: (attempt: number) => void): IRetryPolicy {
  return {
    nextRetryDelayInMilliseconds(context: RetryContext): number {
      onRetry(context.previousRetryCount + 1);
      return reconnectDelayMs(context.previousRetryCount);
    },
  };
}

export const defaultLiveConnectionFactory: LiveConnectionFactory = ({ url, onRetry }) =>
  new HubConnectionBuilder()
    .withUrl(url)
    .withAutomaticReconnect(retryPolicy(onRetry))
    .configureLogging(LogLevel.Warning)
    .build();

/** Swap in a fake connection in unit tests; the default builds a real `HubConnection`. */
export const LIVE_CONNECTION_FACTORY = new InjectionToken<LiveConnectionFactory>(
  'LIVE_CONNECTION_FACTORY',
  { providedIn: 'root', factory: () => defaultLiveConnectionFactory },
);
