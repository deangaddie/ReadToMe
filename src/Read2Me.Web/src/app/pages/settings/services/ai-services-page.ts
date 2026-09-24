import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AiServicesStore } from '@app/ai-services/ai-services-store';
import { WatchdogKind } from '@app/live/live-messages';
import { DockerControls } from '@app/ui/docker-controls/docker-controls';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';

const LOG_KIND: Record<WatchdogKind, StatusKind> = {
  recoveryStarted: 'warn',
  containerRestarted: 'busy',
  serviceHealthy: 'ok',
  serviceDown: 'error',
};

/**
 * `/settings/services` (ticket 25): every managed container with its live status and lifecycle
 * controls, the one-GPU reminder, and the session's watchdog log. Everything reads and writes the
 * app-wide {@link AiServicesStore}, so a chip here and the same chip in a config editor or the
 * activity drawer never disagree.
 */
@Component({
  selector: 'app-ai-services-page',
  imports: [
    DatePipe,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    DockerControls,
    EmptyState,
    PageHeader,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'services-page' },
  template: `
    <r2m-page-header
      title="AI services"
      subtitle="The Docker containers the watchdog manages. Start only what the current step needs."
    >
      <button
        actions
        mat-stroked-button
        type="button"
        [disabled]="store.refreshingAll()"
        (click)="store.refreshAll()"
        data-action="refresh-all"
      >
        <mat-icon>refresh</mat-icon> Refresh all
      </button>
    </r2m-page-header>

    <p class="services-page__note">
      <mat-icon aria-hidden="true">memory</mat-icon>
      <span>
        <strong>GPU: one model at a time.</strong> The card fits one model, so a GPU service is only
        started once the others are shut down — the preflight sheet does this for you before each AI
        action.
      </span>
    </p>

    @if (store.error(); as message) {
      <r2m-empty-state icon="cloud_off" headline="Services unavailable" [hint]="message" />
    } @else if (store.services() === null) {
      <p class="services-page__loading">Loading services…</p>
    } @else if (store.services()!.length === 0) {
      <r2m-empty-state icon="dns" headline="No managed services" />
    } @else {
      <div class="services-page__grid">
        @for (svc of store.services(); track svc.name) {
          <article class="services-page__card" [attr.data-service]="svc.name">
            <header class="services-page__card-head">
              <mat-icon aria-hidden="true">{{ svc.usesGpu ? 'memory' : 'dns' }}</mat-icon>
              <h2 class="services-page__name">{{ svc.name }}</h2>
              @if (svc.usesGpu) {
                <r2m-status-chip status="info" label="GPU" compact tooltip="Needs the GPU" />
              }
            </header>
            <dl class="services-page__meta">
              <dt>Container</dt>
              <dd>{{ svc.containerName }}</dd>
              <dt>Base URL</dt>
              <dd>{{ svc.baseUrl }}</dd>
            </dl>
            <footer class="services-page__card-foot">
              <r2m-docker-controls
                [status]="store.statusOf(svc.name)"
                [busy]="store.isBusy(svc.name)"
                (start)="store.start(svc.name)"
                (restart)="store.restart(svc.name)"
                (shutdown)="store.shutdown(svc.name)"
                (refresh)="store.refresh(svc.name)"
              />
            </footer>
          </article>
        }
      </div>
    }

    <section class="services-page__log" aria-labelledby="watchdog-log-title">
      <h2 id="watchdog-log-title" class="services-page__log-title">Watchdog log</h2>
      @if (store.log().length === 0) {
        <p class="services-page__muted">
          Nothing yet this session. Recoveries, restarts and outages appear here as they happen.
        </p>
      } @else {
        <ol class="services-page__log-list">
          @for (entry of store.log(); track entry.at.getTime() + entry.service + entry.kind) {
            <li class="services-page__log-row" [attr.data-kind]="entry.kind">
              <time [attr.datetime]="entry.at.toISOString()">{{
                entry.at | date: 'HH:mm:ss'
              }}</time>
              <span class="services-page__log-service">{{ entry.service }}</span>
              <r2m-status-chip [status]="logKind(entry.kind)" [label]="entry.label" compact />
              @if (entry.reason) {
                <span class="services-page__muted">{{ entry.reason }}</span>
              }
            </li>
          }
        </ol>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .services-page__note {
      display: flex;
      align-items: flex-start;
      gap: var(--r2m-space-2);
      margin: 0 0 var(--r2m-space-4);
      padding: var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface-low);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .services-page__note mat-icon {
      color: var(--r2m-accent);
      flex: none;
    }
    .services-page__loading,
    .services-page__muted {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .services-page__grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
      gap: var(--r2m-space-4);
    }
    .services-page__card {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-3);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
    }
    .services-page__card-head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .services-page__card-head mat-icon {
      color: var(--r2m-text-muted);
    }
    .services-page__name {
      flex: 1;
      margin: 0;
      font-size: var(--r2m-text-md);
      font-weight: 600;
    }
    .services-page__meta {
      display: grid;
      grid-template-columns: auto 1fr;
      gap: var(--r2m-space-1) var(--r2m-space-3);
      margin: 0;
      font-size: var(--r2m-text-sm);
    }
    .services-page__meta dt {
      color: var(--r2m-text-muted);
    }
    .services-page__meta dd {
      margin: 0;
      font-family: var(--r2m-font-mono);
      overflow-wrap: anywhere;
    }
    .services-page__card-foot {
      display: flex;
      justify-content: flex-end;
      margin-top: auto;
    }
    .services-page__log {
      margin-top: var(--r2m-space-6);
    }
    .services-page__log-title {
      margin: 0 0 var(--r2m-space-2);
      font-size: var(--r2m-text-md);
      font-weight: 600;
    }
    .services-page__log-list {
      list-style: none;
      margin: 0;
      padding: 0;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      max-height: 320px;
      overflow: auto;
    }
    .services-page__log-row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-bottom: 1px solid var(--r2m-outline);
      font-size: var(--r2m-text-sm);
    }
    .services-page__log-row:last-child {
      border-bottom: 0;
    }
    .services-page__log-row time {
      font-family: var(--r2m-font-mono);
      color: var(--r2m-text-muted);
    }
    .services-page__log-service {
      font-weight: 500;
    }
  `,
})
export class AiServicesPage {
  protected readonly store = inject(AiServicesStore);

  constructor() {
    void this.store.ensureLoaded();
  }

  protected logKind(kind: WatchdogKind): StatusKind {
    return LOG_KIND[kind] ?? 'neutral';
  }
}
