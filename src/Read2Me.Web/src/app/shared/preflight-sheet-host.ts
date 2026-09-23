import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { AiTaskKind, PreflightApi, PreflightPlanDto, toApiError } from '@app/api';
import { PreflightMessage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import {
  PreflightPhase,
  PreflightPlan,
  PreflightServiceProgress,
  PreflightSheet,
} from '@app/ui/preflight-sheet/preflight-sheet';
import { ToastService } from '@app/ui/toast/toast.service';

export interface PreflightSheetData {
  kind: AiTaskKind;
  label: string;
  plan: PreflightPlanDto;
}

/** The rows the run will walk, in the order the host walks them: conflicts stopped first. */
export function initialProgress(plan: PreflightPlanDto): PreflightServiceProgress[] {
  return [
    ...plan.conflicts.map((c) => ({ name: c.name, stage: 'waitingToStop' as const })),
    ...plan.toStart.map((s) => ({ name: s.name, stage: 'waitingToStart' as const })),
  ];
}

/**
 * Bottom-sheet body behind {@link Preflight.ensureReady}: the presentational
 * `r2m-preflight-sheet` fed by the plan, then by the run's `preflight` hub messages. Dismisses
 * with `true` once the host reports the run done and ok; anything else is `false`.
 */
@Component({
  selector: 'app-preflight-sheet-host',
  imports: [PreflightSheet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-preflight-sheet
      [taskLabel]="data.label"
      [phase]="phase()"
      [plan]="plan"
      [progress]="progress()"
      [error]="error()"
      (start)="start()"
      (cancel)="cancel()"
      (close)="ref.dismiss(false)"
    />
  `,
})
export class PreflightSheetHost {
  protected readonly data = inject<PreflightSheetData>(MAT_BOTTOM_SHEET_DATA);
  protected readonly ref =
    inject<MatBottomSheetRef<PreflightSheetHost, boolean>>(MatBottomSheetRef);
  private readonly api = inject(PreflightApi);
  private readonly live = inject(LiveService);
  private readonly toast = inject(ToastService);

  protected readonly plan: PreflightPlan = { ...this.data.plan };
  protected readonly phase = signal<PreflightPhase>('plan');
  protected readonly progress = signal<PreflightServiceProgress[]>(initialProgress(this.data.plan));
  protected readonly error = signal<string | null>(null);

  /** The run this sheet follows; messages that arrive before the 202 answers wait here. */
  private run: string | null = null;
  private early: PreflightMessage[] = [];

  constructor() {
    this.live
      .on('preflight')
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe((m) => {
        if (this.run === null) this.early.push(m);
        else if (m.run === this.run) this.apply(m);
      });
  }

  protected async start(): Promise<void> {
    const connectionId = this.live.connectionId();
    if (connectionId === null) {
      this.toast.warn('Live updates are disconnected — try again in a moment');
      return;
    }
    this.phase.set('running');
    try {
      const { run } = await this.api.run(this.data.kind, { connectionId });
      this.run = run;
      const early = this.early;
      this.early = [];
      for (const m of early) if (m.run === run) this.apply(m);
    } catch (e) {
      this.phase.set('failed');
      this.error.set(toApiError(e).message);
    }
  }

  protected cancel(): void {
    if (this.phase() === 'running') {
      // Docker ops are not safely abortable: the host finishes them; only this task is not started.
      this.toast.info('Services continue starting in the background');
    }
    this.ref.dismiss(false);
  }

  private apply(m: PreflightMessage): void {
    if (m.kind === 'stage' && m.name && m.stage) {
      const name = m.name;
      const stage = m.stage;
      const error = m.error ?? null;
      this.progress.update((rows) =>
        rows.some((r) => r.name === name)
          ? rows.map((r) => (r.name === name ? { ...r, stage, error } : r))
          : [...rows, { name, stage, error }],
      );
      return;
    }
    if (m.kind === 'done') {
      if (m.ok) {
        this.phase.set('done');
        this.ref.dismiss(true);
      } else {
        this.phase.set('failed');
        this.error.set(m.reason ?? null);
      }
    }
  }
}
