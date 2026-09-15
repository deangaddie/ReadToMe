import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { AiServiceDto, AiServicesApi, toApiError } from '@app/api';
import { WatchdogState } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';
import { watchdogView } from './activity-jobs';

interface ServiceRow {
  name: string;
  containerName: string;
  usesGpu: boolean;
  status: StatusKind;
  label: string;
}

export function serviceRows(
  services: readonly AiServiceDto[],
  watchdog: WatchdogState,
): ServiceRow[] {
  const byName = new Map(Object.entries(watchdog).map(([k, v]) => [k.toLowerCase(), v]));
  return services.map((s) => {
    const { status, label } = watchdogView(byName.get(s.name.toLowerCase()));
    return { name: s.name, containerName: s.containerName, usesGpu: s.usesGpu, status, label };
  });
}

/**
 * The drawer's Services tab (ticket 14): the managed AI services with the watchdog's last word on
 * each. Read-only here — the services page (ticket 25) starts, restarts and probes them.
 */
@Component({
  selector: 'app-services-tab',
  imports: [MatButtonModule, MatIconModule, RouterLink, EmptyState, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-services' },
  template: `
    @if (error(); as message) {
      <r2m-empty-state compact icon="cloud_off" headline="Services unavailable" [hint]="message" />
    } @else if (services() === null) {
      <p class="activity-services__loading">Loading services…</p>
    } @else if (rows().length === 0) {
      <r2m-empty-state compact icon="dns" headline="No managed services" />
    } @else {
      <ul class="activity-services__list">
        @for (row of rows(); track row.name) {
          <li class="activity-services__row">
            <mat-icon class="activity-services__icon" aria-hidden="true">{{
              row.usesGpu ? 'memory' : 'dns'
            }}</mat-icon>
            <span class="activity-services__name">
              <span>{{ row.name }}</span>
              <span class="activity-services__container">{{ row.containerName }}</span>
            </span>
            <r2m-status-chip [status]="row.status" [label]="row.label" compact />
          </li>
        }
      </ul>
    }
    <div class="activity-services__foot">
      <a mat-stroked-button routerLink="/settings/services">
        <mat-icon>settings</mat-icon>Manage services
      </a>
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .activity-services__loading {
      margin: var(--r2m-space-4);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
      text-align: center;
    }
    .activity-services__list {
      flex: 1 1 auto;
      min-height: 0;
      overflow: auto;
      list-style: none;
      margin: 0;
      padding: var(--r2m-space-2);
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
    }
    .activity-services__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface);
    }
    .activity-services__icon {
      color: var(--r2m-text-muted);
    }
    .activity-services__name {
      flex: 1;
      min-width: 0;
      display: flex;
      flex-direction: column;
      font-weight: 500;
    }
    .activity-services__container {
      font-size: var(--r2m-text-xs);
      font-weight: 400;
      color: var(--r2m-text-muted);
      font-family: var(--r2m-font-mono);
    }
    .activity-services__foot {
      display: flex;
      justify-content: flex-end;
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-top: 1px solid var(--r2m-outline);
    }
  `,
})
export class ServicesTab {
  private readonly api = inject(AiServicesApi);
  private readonly live = inject(LiveService);

  protected readonly services = signal<AiServiceDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly rows = computed(() =>
    serviceRows(this.services() ?? [], this.live.watchdog()),
  );

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.services.set(await this.api.list());
    } catch (error) {
      this.error.set(toApiError(error).message);
    }
  }
}
