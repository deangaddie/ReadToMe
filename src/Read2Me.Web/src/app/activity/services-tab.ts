import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { AiServicesStore } from '@app/ai-services/ai-services-store';
import { DockerControls } from '@app/ui/docker-controls/docker-controls';
import { EmptyState } from '@app/ui/empty-state/empty-state';

/**
 * The drawer's Services tab (tickets 14, 25): the compact form of `/settings/services` — one row
 * per managed container with the same hub-fed status chip and Start / Restart / Shutdown / Refresh,
 * all through the app-wide {@link AiServicesStore}.
 */
@Component({
  selector: 'app-services-tab',
  imports: [MatButtonModule, MatIconModule, RouterLink, DockerControls, EmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-services' },
  template: `
    @if (store.error(); as message) {
      <r2m-empty-state compact icon="cloud_off" headline="Services unavailable" [hint]="message" />
    } @else if (store.services() === null) {
      <p class="activity-services__loading">Loading services…</p>
    } @else if (store.services()!.length === 0) {
      <r2m-empty-state compact icon="dns" headline="No managed services" />
    } @else {
      <ul class="activity-services__list">
        @for (svc of store.services(); track svc.name) {
          <li class="activity-services__row" [attr.data-service]="svc.name">
            <mat-icon class="activity-services__icon" aria-hidden="true">{{
              svc.usesGpu ? 'memory' : 'dns'
            }}</mat-icon>
            <span class="activity-services__name">
              <span>{{ svc.name }}</span>
              <span class="activity-services__container">{{ svc.containerName }}</span>
            </span>
            <r2m-docker-controls
              [status]="store.statusOf(svc.name)"
              [busy]="store.isBusy(svc.name)"
              (start)="store.start(svc.name)"
              (restart)="store.restart(svc.name)"
              (shutdown)="store.shutdown(svc.name)"
              (refresh)="store.refresh(svc.name)"
            />
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
  protected readonly store = inject(AiServicesStore);

  constructor() {
    void this.store.ensureLoaded();
  }
}
