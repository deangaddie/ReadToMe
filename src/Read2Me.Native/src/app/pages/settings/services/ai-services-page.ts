import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { AiServicesStore, type WatchdogLogEntry } from '@app/ai-services/ai-services-store';
import type { AiServiceDto, AiServiceStatus } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { adoptStyles } from '@app/core/styles';
import type { WatchdogKind } from '@app/live/live-messages';
import { dockerControls } from '@app/ui/docker-controls';
import { keyValue } from '@app/ui/key-value';
import { type StatusKind, emptyState, icon, statusChip } from '@app/ui/partials';
import css from './ai-services-page.css' with { type: 'text' };

adoptStyles(css);

const LOG_KIND: Record<WatchdogKind, StatusKind> = {
  recoveryStarted: 'warn',
  containerRestarted: 'busy',
  serviceHealthy: 'ok',
  serviceDown: 'error',
};

/** One service card's inputs, derived in the render effect (see {@link AiServicesPage.cards}). */
interface ServiceCard {
  svc: AiServiceDto;
  status: AiServiceStatus;
  busy: boolean;
}

/** Angular's `date: 'HH:mm:ss'`: the local wall clock, zero-padded. */
export function formatLogTime(at: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `${two(at.getHours())}:${two(at.getMinutes())}:${two(at.getSeconds())}`;
}

/**
 * `/settings/services` (ticket 25): every managed container with its live status and lifecycle
 * controls, the one-GPU reminder, and the session's watchdog log. Everything reads and writes the
 * app-wide {@link AiServicesStore}, so a chip here and the same chip in a config editor or the
 * activity drawer never disagree.
 */
export class AiServicesPage extends R2mElement {
  readonly store = use(AiServicesStore);

  protected override connected(): void {
    this.classList.add('services-page');
    void this.store.ensureLoaded();
  }

  protected template() {
    const error = this.store.error();
    const services = this.store.services();
    const log = this.store.log();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">AI services</h1>
        <p class="r2m-page-header__subtitle">
          The Docker containers the watchdog manages. Start only what the current step needs.
        </p>
        <span class="r2m-page-header__actions">
          <button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="refresh-all"
            ?disabled=${this.store.refreshingAll()}
            @click=${() => void this.store.refreshAll()}
          >
            ${icon('refresh')} Refresh all
          </button>
        </span>
      </header>

      <p class="services-page__note">
        ${icon('memory')}
        <span>
          <strong>GPU: one model at a time.</strong> The card fits one model, so a GPU service is
          only started once the others are shut down — the preflight sheet does this for you before
          each AI action.
        </span>
      </p>

      ${
        error
          ? emptyState({ icon: 'cloud_off', headline: 'Services unavailable', hint: error })
          : services === null
            ? html`<p class="services-page__loading">Loading services…</p>`
            : services.length === 0
              ? emptyState({ icon: 'dns', headline: 'No managed services' })
              : html`<div class="services-page__grid">
                  ${repeat(
                    this.cards(services),
                    (card) => card.svc.name,
                    (card) => this.card(card),
                  )}
                </div>`
      }

      <section class="services-page__log" aria-labelledby="watchdog-log-title">
        <h2 id="watchdog-log-title" class="services-page__log-title">Watchdog log</h2>
        ${
          log.length === 0
            ? html`<p class="services-page__muted">
                Nothing yet this session. Recoveries, restarts and outages appear here as they
                happen.
              </p>`
            : html`<ol class="services-page__log-list">
                ${repeat(
                  log,
                  (entry) => `${entry.at.getTime()}${entry.service}${entry.kind}`,
                  (entry) => this.logRow(entry),
                )}
              </ol>`
        }
      </section>
    `;
  }

  /**
   * Status and busy are read here, in the render effect: lit's `repeat` runs its item template
   * at commit time, outside the effect, so a signal first read there would not be tracked.
   */
  private cards(services: AiServiceDto[]): ServiceCard[] {
    return services.map((svc) => ({
      svc,
      status: this.store.statusOf(svc.name),
      busy: this.store.isBusy(svc.name),
    }));
  }

  private card({ svc, status, busy }: ServiceCard) {
    return html`<article class="services-page__card" data-service=${svc.name}>
      <header class="services-page__card-head">
        ${icon(svc.usesGpu ? 'memory' : 'dns')}
        <h2 class="services-page__name">${svc.name}</h2>
        ${
          svc.usesGpu
            ? statusChip({ status: 'info', label: 'GPU', compact: true, tooltip: 'Needs the GPU' })
            : nothing
        }
      </header>
      ${keyValue(
        [
          { label: 'Container', value: svc.containerName, mono: true },
          { label: 'Base URL', value: svc.baseUrl, mono: true },
        ],
        { dense: true },
      )}
      <footer class="services-page__card-foot">
        ${dockerControls({
          status,
          busy,
          onStart: () => void this.store.start(svc.name),
          onRestart: () => void this.store.restart(svc.name),
          onShutdown: () => void this.store.shutdown(svc.name),
          onRefresh: () => void this.store.refresh(svc.name),
        })}
      </footer>
    </article>`;
  }

  private logRow(entry: WatchdogLogEntry) {
    return html`<li class="services-page__log-row" data-kind=${entry.kind}>
      <time datetime=${entry.at.toISOString()}>${formatLogTime(entry.at)}</time>
      <span class="services-page__log-service">${entry.service}</span>
      ${statusChip({ status: LOG_KIND[entry.kind] ?? 'neutral', label: entry.label, compact: true })}
      ${entry.reason ? html`<span class="services-page__muted">${entry.reason}</span>` : nothing}
    </li>`;
  }
}
define('r2m-ai-services-page', AiServicesPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-ai-services-page': AiServicesPage;
  }
}
