import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  output,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';

/** Mirrors `AiServiceStatus` in `Read2Me.Services/Health/IAiServiceControl.cs` (serialised as the member name). */
export type AiServiceStatus = 'NotFound' | 'Stopped' | 'Starting' | 'Ready' | 'Unknown';

const STATUS_VIEW: Record<AiServiceStatus, { kind: StatusKind; label: string }> = {
  Ready: { kind: 'ok', label: 'Ready' },
  Starting: { kind: 'busy', label: 'Starting' },
  Stopped: { kind: 'neutral', label: 'Stopped' },
  NotFound: { kind: 'warn', label: 'Not found' },
  Unknown: { kind: 'neutral', label: 'Unknown' },
};

/**
 * Presentational Start / Restart / Shutdown / Refresh controls for a managed container (design §7).
 * Ticket 25 wires the outputs to the AI services store.
 */
@Component({
  selector: 'r2m-docker-controls',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-docker-controls' },
  template: `
    @if (serviceName()) {
      <span class="r2m-docker-controls__name">{{ serviceName() }}</span>
    }
    <r2m-status-chip [status]="view().kind" [label]="view().label" compact />
    <span class="r2m-docker-controls__buttons">
      <button
        mat-icon-button
        type="button"
        [disabled]="busy() || !canStart()"
        (click)="start.emit()"
        aria-label="Start"
        matTooltip="Start"
      >
        <mat-icon>play_arrow</mat-icon>
      </button>
      <button
        mat-icon-button
        type="button"
        [disabled]="busy() || !canStop()"
        (click)="restart.emit()"
        aria-label="Restart"
        matTooltip="Restart"
      >
        <mat-icon>restart_alt</mat-icon>
      </button>
      <button
        mat-icon-button
        type="button"
        [disabled]="busy() || !canStop()"
        (click)="shutdown.emit()"
        aria-label="Shutdown"
        matTooltip="Shutdown"
      >
        <mat-icon>stop</mat-icon>
      </button>
      <button
        mat-icon-button
        type="button"
        [disabled]="busy()"
        (click)="refresh.emit()"
        aria-label="Refresh status"
        matTooltip="Refresh status"
      >
        <mat-icon>refresh</mat-icon>
      </button>
    </span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .r2m-docker-controls__name {
      font-weight: 500;
    }
    .r2m-docker-controls__buttons {
      display: inline-flex;
    }
    .r2m-docker-controls__buttons button {
      --mdc-icon-button-state-layer-size: 32px;
      width: 32px;
      height: 32px;
      padding: 4px;
    }
  `,
})
export class DockerControls {
  readonly status = input<AiServiceStatus>('Unknown');
  readonly busy = input(false, { transform: booleanAttribute });
  readonly serviceName = input<string>();

  readonly start = output<void>();
  readonly restart = output<void>();
  readonly shutdown = output<void>();
  readonly refresh = output<void>();

  protected readonly view = computed(() => STATUS_VIEW[this.status()]);
  protected readonly canStart = computed(() => this.status() === 'Stopped');
  protected readonly canStop = computed(
    () => this.status() === 'Starting' || this.status() === 'Ready',
  );
}
