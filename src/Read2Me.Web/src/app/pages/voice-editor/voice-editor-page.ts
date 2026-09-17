import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import {
  PreviewResponse,
  PreviewStepRequest,
  StepCatalogEntryDto,
  VoiceDto,
  VoiceEditorApi,
  VoicesApi,
  toApiError,
} from '@app/api';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { PageHeader } from '@app/ui/page-header/page-header';
import { SettingsForm, SettingsValues } from '@app/ui/settings-form/settings-form';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import {
  StepValues,
  Ticks,
  applyBlockedReason,
  buildPreviewSteps,
  hissRedundant,
  originalAudioUrl,
  sameRender,
  seedValues,
  stageFor,
  toStepSchema,
} from './editor-logic';

/** The last render: what was asked for and what came back. */
interface Render {
  request: PreviewStepRequest[];
  response: PreviewResponse;
}

/**
 * `/projects/{folder}/voices/{voiceId}/editor` (ticket 18, design §6.4): the step checklist on the
 * left with the selected step's dials on the right, an Original player plus one "After <step>"
 * player per rendered stage, and Apply gated on a render that matches the current ticks and dials.
 * The host re-checks that gate through `previewId`; this page only explains it.
 */
@Component({
  selector: 'app-voice-editor-page',
  imports: [
    RouterLink,
    MatButtonModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    AudioPlayer,
    EmptyState,
    PageHeader,
    SettingsForm,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'voice-editor' },
  template: `
    <r2m-page-header title="Edit voice audio" [subtitle]="voice()?.name">
      <ng-container actions>
        <a mat-button data-action="back" [routerLink]="backLink()">
          <mat-icon>arrow_back</mat-icon> Back
        </a>
      </ng-container>
    </r2m-page-header>

    @if (loading()) {
      <mat-progress-bar mode="indeterminate" aria-label="Loading voice" />
    } @else if (loadError(); as error) {
      <r2m-empty-state icon="voice_over_off" headline="Cannot edit this voice" [hint]="error">
        <a mat-button action data-action="back-empty" [routerLink]="backLink()">Back to the cast</a>
      </r2m-empty-state>
    } @else if (voice(); as voice) {
      <div class="voice-editor__toolbar">
        <button
          mat-flat-button
          type="button"
          data-action="preview"
          [disabled]="!anyTicked() || busy()"
          (click)="preview()"
        >
          @if (rendering()) {
            <mat-spinner diameter="18" />
          } @else {
            <mat-icon>play_circle</mat-icon>
          }
          Preview
        </button>
        <span [matTooltip]="applyBlocked() ?? ''" matTooltipPosition="above">
          <button
            mat-stroked-button
            type="button"
            data-action="apply"
            [disabled]="applyBlocked() !== null"
            (click)="apply()"
          >
            <mat-icon>check</mat-icon> Apply
          </button>
        </span>
        @if (voice.isEdited) {
          <r2m-status-chip status="warn" label="Edited" icon="tune" compact data-testid="edited-chip" />
          <button
            mat-button
            type="button"
            class="voice-editor__danger"
            data-action="restore"
            [disabled]="busy()"
            (click)="restore()"
          >
            <mat-icon>history</mat-icon> Restore original
          </button>
        }
        @if (actionError(); as error) {
          <r2m-status-chip status="error" [label]="error" data-testid="action-error" />
        } @else if (actionNote(); as note) {
          <r2m-status-chip status="ok" icon="check" compact [label]="note" data-testid="action-note" />
        }
      </div>

      <div class="voice-editor__body">
        <section class="voice-editor__steps" aria-label="Steps">
          @for (entry of catalog(); track entry.stepId) {
            <div
              class="voice-editor__step"
              [class.voice-editor__step--selected]="entry.stepId === selectedStepId()"
              [attr.data-step]="entry.stepId"
              (click)="selectedStepId.set(entry.stepId)"
              (keydown.enter)="selectedStepId.set(entry.stepId)"
              tabindex="0"
              role="button"
            >
              <mat-checkbox
                [checked]="!!ticks()[entry.stepId]"
                [disabled]="busy()"
                (change)="tick(entry.stepId, $event.checked)"
                (click)="$event.stopPropagation()"
                [attr.data-tick]="entry.stepId"
              >
                {{ entry.label }}
              </mat-checkbox>
              <p class="voice-editor__blurb">{{ entry.blurb }}</p>
              @if (stageFor(entry.stepId); as stage) {
                @if (!stage.applied) {
                  <r2m-status-chip
                    status="warn"
                    icon="skip_next"
                    compact
                    [label]="'Skipped — ' + (stage.reason ?? 'no reason given')"
                    data-testid="skipped-chip"
                  />
                }
              }
              @if (entry.stepId === 'hiss-reduce' && hissRedundant()) {
                <p class="voice-editor__hint" data-testid="hiss-hint">
                  Denoise already handles most hiss; try one without the other first.
                </p>
              }
            </div>
          }
        </section>

        <section class="voice-editor__dials" aria-label="Dials">
          @if (selected(); as entry) {
            <h3>{{ entry.label }}</h3>
            @if (entry.dials.length === 0) {
              <p class="voice-editor__blurb">This step has nothing to adjust.</p>
            } @else {
              <r2m-settings-form
                mode="defaults"
                [schema]="selectedSchema()!"
                [values]="values()[entry.stepId] ?? {}"
                [disabled]="busy()"
                (valuesChange)="setValues(entry.stepId, $event)"
              />
            }
          } @else {
            <p class="voice-editor__blurb">Select a step to adjust its dials.</p>
          }
        </section>
      </div>

      <section class="voice-editor__players" aria-label="Players">
        <r2m-audio-player
          [src]="originalSrc()"
          [cacheKey]="audioVersion()"
          [label]="voice.isEdited ? 'Original (before edits)' : 'Original'"
          data-testid="original-player"
        />
        @for (stage of stages(); track stage.stepId) {
          <r2m-audio-player
            [src]="stage.url"
            [label]="'After ' + labelFor(stage.stepId)"
            [attr.data-stage]="stage.stepId"
          />
        }
        @if (stale()) {
          <p class="voice-editor__hint" data-testid="stale-hint">
            Ticks or dials changed since this render — preview again before applying.
          </p>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
    }
    .voice-editor__toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .voice-editor__danger {
      color: var(--r2m-status-error);
    }
    .voice-editor__body {
      display: grid;
      grid-template-columns: minmax(280px, 360px) 1fr;
      gap: var(--r2m-space-4);
    }
    @media (max-width: 720px) {
      .voice-editor__body {
        grid-template-columns: 1fr;
      }
    }
    .voice-editor__steps {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .voice-editor__step {
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      cursor: pointer;
    }
    .voice-editor__step--selected {
      border-color: var(--r2m-accent);
    }
    .voice-editor__blurb,
    .voice-editor__hint {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .voice-editor__hint {
      margin-top: var(--r2m-space-1);
    }
    .voice-editor__dials h3 {
      margin: 0 0 var(--r2m-space-2);
    }
    .voice-editor__players {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
  `,
})
export class VoiceEditorPage {
  private readonly voices = inject(VoicesApi);
  private readonly editor = inject(VoiceEditorApi);
  private readonly confirm = inject(ConfirmService);

  readonly folder = input.required<string>();
  readonly voiceId = input.required<string>();

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);
  /** The last apply/restore outcome, shown inline (design §8: own-action outcomes are not toasts). */
  protected readonly actionNote = signal<string | null>(null);
  protected readonly voice = signal<VoiceDto | null>(null);
  protected readonly catalog = signal<StepCatalogEntryDto[]>([]);

  protected readonly ticks = signal<Ticks>({});
  protected readonly values = signal<StepValues>({});
  protected readonly selectedStepId = signal<string | null>(null);
  protected readonly render = signal<Render | null>(null);
  protected readonly rendering = signal(false);
  protected readonly writing = signal(false);
  /** Bumped whenever the voice's files change under the same names, so the players refetch. */
  protected readonly audioVersion = signal(0);

  protected readonly busy = computed(() => this.rendering() || this.writing());
  protected readonly anyTicked = computed(() => Object.values(this.ticks()).some(Boolean));
  protected readonly currentRequest = computed(() =>
    buildPreviewSteps(this.catalog(), this.ticks(), this.values()),
  );
  protected readonly stale = computed(() => {
    const render = this.render();
    return render !== null && !sameRender(render.request, this.currentRequest());
  });
  protected readonly applyBlocked = computed(() =>
    applyBlockedReason({
      anyTicked: this.anyTicked(),
      hasRender: this.render() !== null,
      stale: this.stale(),
      busy: this.busy(),
    }),
  );
  protected readonly hissRedundant = computed(() => hissRedundant(this.ticks()));
  protected readonly stages = computed(() => this.render()?.response.stages ?? []);
  protected readonly selected = computed(
    () => this.catalog().find((e) => e.stepId === this.selectedStepId()) ?? null,
  );
  protected readonly selectedSchema = computed(() => {
    const entry = this.selected();
    return entry ? toStepSchema(entry) : null;
  });
  protected readonly originalSrc = computed(() => {
    const voice = this.voice();
    return voice ? originalAudioUrl(this.folder(), voice) : null;
  });
  protected readonly backLink = computed(() => {
    const characterId = this.voice()?.characterId;
    return characterId
      ? ['/projects', this.folder(), 'cast', characterId]
      : ['/projects', this.folder(), 'cast'];
  });

  constructor() {
    effect(() => {
      const folder = this.folder();
      const voiceId = this.voiceId();
      untracked(() => void this.load(folder, voiceId));
    });
  }

  protected stageFor(stepId: string) {
    return stageFor(this.render()?.response.stages ?? null, stepId);
  }

  protected labelFor(stepId: string): string {
    return this.catalog().find((e) => e.stepId === stepId)?.label ?? stepId;
  }

  protected tick(stepId: string, checked: boolean): void {
    this.ticks.update((t) => ({ ...t, [stepId]: checked }));
    this.selectedStepId.set(stepId);
  }

  protected setValues(stepId: string, values: SettingsValues): void {
    this.values.update((v) => ({ ...v, [stepId]: values }));
  }

  protected async preview(): Promise<void> {
    const voice = this.voice();
    if (!voice || !this.anyTicked() || this.busy()) return;
    const request = this.currentRequest();
    this.rendering.set(true);
    this.actionError.set(null);
    this.actionNote.set(null);
    try {
      const response = await this.editor.preview(this.folder(), voice.id, request);
      this.render.set({ request, response });
    } catch (e) {
      this.actionError.set(toApiError(e).message);
    } finally {
      this.rendering.set(false);
    }
  }

  protected async apply(): Promise<void> {
    const voice = this.voice();
    const render = this.render();
    if (!voice || !render || this.applyBlocked() !== null) return;
    this.writing.set(true);
    this.actionError.set(null);
    this.actionNote.set(null);
    try {
      this.voice.set(await this.editor.apply(this.folder(), voice.id, render.response.previewId));
      this.audioVersion.update((v) => v + 1);
      this.actionNote.set('Voice audio updated.');
    } catch (e) {
      this.actionError.set(toApiError(e).message);
    } finally {
      this.writing.set(false);
    }
  }

  protected async restore(): Promise<void> {
    const voice = this.voice();
    if (!voice || this.busy()) return;
    const ok = await this.confirm.confirm({
      title: 'Restore original audio?',
      message: 'The edited audio is discarded and the voice goes back to its original recording.',
      confirmLabel: 'Restore',
      destructive: true,
    });
    if (!ok) return;
    this.writing.set(true);
    this.actionError.set(null);
    this.actionNote.set(null);
    try {
      this.voice.set(await this.editor.restore(this.folder(), voice.id));
      this.resetChain();
      this.audioVersion.update((v) => v + 1);
      this.actionNote.set('Original audio restored.');
    } catch (e) {
      this.actionError.set(toApiError(e).message);
    } finally {
      this.writing.set(false);
    }
  }

  private resetChain(): void {
    this.ticks.set({});
    this.values.set(seedValues(this.catalog()));
    this.render.set(null);
  }

  private async load(folder: string, voiceId: string): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    this.voice.set(null);
    this.render.set(null);
    try {
      const [voice, catalog] = await Promise.all([
        this.voices.get(folder, voiceId),
        this.editor.catalog(),
      ]);
      this.catalog.set(catalog);
      this.resetChain();
      this.selectedStepId.set(catalog[0]?.stepId ?? null);
      if (!voice.audioFileName) {
        this.loadError.set('This voice has no audio to edit.');
        return;
      }
      this.voice.set(voice);
    } catch (e) {
      const error = toApiError(e);
      this.loadError.set(error.status === 404 ? 'That voice no longer exists.' : error.message);
    } finally {
      this.loading.set(false);
    }
  }
}
