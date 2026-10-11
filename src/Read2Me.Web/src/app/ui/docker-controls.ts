import { html, nothing } from 'lit-html';
import type { AiServiceStatus } from '@app/api';
import { type StatusKind, icon, statusChip } from './partials';

export const SERVICE_STATUS_VIEW: Record<AiServiceStatus, { kind: StatusKind; label: string }> = {
  Ready: { kind: 'ok', label: 'Ready' },
  Starting: { kind: 'busy', label: 'Starting' },
  Stopped: { kind: 'neutral', label: 'Stopped' },
  Recovering: { kind: 'busy', label: 'Recovering' },
  Down: { kind: 'error', label: 'Down' },
  NotFound: { kind: 'warn', label: 'Not found' },
  Unknown: { kind: 'neutral', label: 'Unknown' },
};

export const canStartService = (status: AiServiceStatus): boolean =>
  status === 'Stopped' || status === 'NotFound';
export const canStopService = (status: AiServiceStatus): boolean =>
  status === 'Starting' || status === 'Ready' || status === 'Down';

export interface DockerControlsOptions {
  status: AiServiceStatus;
  busy?: boolean;
  /** Keeps just the chip and Refresh. */
  statusOnly?: boolean;
  serviceName?: string;
  onStart?: () => void;
  onRestart?: () => void;
  onShutdown?: () => void;
  onRefresh: () => void;
}

const iconButton = (
  label: string,
  name: Parameters<typeof icon>[0],
  disabled: boolean,
  on?: () => void,
) =>
  html`<button
    type="button"
    class="r2m-icon-button r2m-docker-controls__button"
    ?disabled=${disabled}
    @click=${on}
    aria-label=${label}
    data-tooltip=${label}
  >
    ${icon(name)}
  </button>`;

/**
 * Presentational Start / Restart / Shutdown / Refresh controls for a managed container (design §7).
 * The services page, the drawer's Services tab and the config editors wire the callbacks to the
 * app-wide AI services store. Button availability follows the state: Stopped / Not found can be
 * started, a live or Down container can be restarted or shut down.
 */
export function dockerControls(o: DockerControlsOptions) {
  const view = SERVICE_STATUS_VIEW[o.status];
  const busy = o.busy ?? false;
  return html`<span class="r2m-docker-controls">
    ${o.serviceName ? html`<span class="r2m-docker-controls__name">${o.serviceName}</span>` : nothing}
    ${statusChip({ status: view.kind, label: view.label, compact: true })}
    <span class="r2m-docker-controls__buttons">
      ${
        o.statusOnly
          ? nothing
          : html`${iconButton('Start', 'play_arrow', busy || !canStartService(o.status), o.onStart)}
            ${iconButton('Restart', 'restart_alt', busy || !canStopService(o.status), o.onRestart)}
            ${iconButton('Shutdown', 'stop', busy || !canStopService(o.status), o.onShutdown)}`
      }
      ${iconButton('Refresh status', 'refresh', busy, o.onRefresh)}
    </span>
  </span>`;
}
