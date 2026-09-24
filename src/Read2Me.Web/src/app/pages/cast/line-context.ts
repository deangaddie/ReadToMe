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
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { CharacterLineDto, CharactersApi, ParagraphContextDto, toApiError } from '@app/api';
import { SpeakerChip } from '@app/ui/speaker-chip/speaker-chip';
import {
  ContextWindow,
  INITIAL_WINDOW,
  canGrowAfter,
  canGrowBefore,
  contextSpeakers,
  growAfter,
  growBefore,
} from './context-paging';

/**
 * A line's surroundings (research §4 "Lines"): the paragraph and its neighbours in the chapter,
 * each with a speaker chip per dialog speaker. Loads on demand; "+ previous" / "+ next" widen the
 * window by the paging steps up to the host's cap. Changing the line resets the window.
 */
@Component({
  selector: 'app-line-context',
  imports: [NgTemplateOutlet, MatButtonModule, MatProgressBarModule, SpeakerChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'line-context' },
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" aria-label="Loading context" />
    }
    @if (error(); as error) {
      <p class="line-context__error">{{ error }}</p>
    }
    @if (context(); as ctx) {
      @for (p of ctx.before; track $index) {
        <ng-container *ngTemplateOutlet="paragraph; context: { $implicit: p, query: false }" />
      }
      <ng-container *ngTemplateOutlet="paragraph; context: { $implicit: ctx.paragraph, query: true }" />
      @for (p of ctx.after; track $index) {
        <ng-container *ngTemplateOutlet="paragraph; context: { $implicit: p, query: false }" />
      }
      <div class="line-context__more">
        @if (canGrowBefore(window())) {
          <button mat-button type="button" data-action="context-previous" (click)="more('before')">
            + previous
          </button>
        }
        @if (canGrowAfter(window())) {
          <button mat-button type="button" data-action="context-next" (click)="more('after')">
            + next
          </button>
        }
      </div>
    } @else if (!loading()) {
      <button mat-button type="button" data-action="load-context" (click)="load()">Load context</button>
    }

    <ng-template #paragraph let-p let-query="query">
      <div class="line-context__paragraph" [class.line-context__paragraph--query]="query">
        <div class="line-context__speakers">
          @for (s of speakers(p); track $index) {
            <r2m-speaker-chip [name]="s.name" [state]="s.unknown ? 'unknown' : 'named'" compact />
          } @empty {
            <span class="line-context__narration">—</span>
          }
        </div>
        <p class="line-context__text">{{ p.text }}</p>
      </div>
    </ng-template>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
      padding: var(--r2m-space-2);
    }
    .line-context__paragraph {
      display: grid;
      grid-template-columns: 110px minmax(0, 1fr);
      gap: var(--r2m-space-2);
      align-items: start;
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border-radius: var(--r2m-radius-sm);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .line-context__paragraph--query {
      background: color-mix(in srgb, var(--r2m-accent) 10%, transparent);
      color: var(--r2m-text);
      font-size: var(--r2m-text-md);
    }
    .line-context__speakers {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-1);
    }
    .line-context__narration {
      color: var(--r2m-text-muted);
    }
    .line-context__text {
      margin: 0;
      white-space: pre-wrap;
    }
    .line-context__more {
      display: flex;
      justify-content: flex-end;
      gap: var(--r2m-space-1);
    }
    .line-context__error {
      margin: 0;
      color: var(--r2m-status-error);
      font-size: var(--r2m-text-sm);
    }
  `,
})
export class LineContext {
  private readonly api = inject(CharactersApi);

  readonly folder = input.required<string>();
  readonly line = input.required<CharacterLineDto>();

  protected readonly context = signal<ParagraphContextDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly window = signal<ContextWindow>(INITIAL_WINDOW);

  protected readonly canGrowBefore = canGrowBefore;
  protected readonly canGrowAfter = canGrowAfter;
  protected readonly speakers = contextSpeakers;

  private readonly lineId = computed(() => this.line().itemId);

  constructor() {
    effect(() => {
      this.lineId();
      untracked(() => {
        this.context.set(null);
        this.error.set(null);
        this.window.set(INITIAL_WINDOW);
      });
    });
  }

  protected async load(): Promise<void> {
    const line = this.line();
    const window = this.window();
    this.loading.set(true);
    this.error.set(null);
    try {
      const ctx = await this.api.context(
        this.folder(),
        line.chapterId,
        line.paragraphId,
        window.before,
        window.after,
      );
      if (this.line().itemId === line.itemId) this.context.set(ctx);
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }

  protected async more(side: 'before' | 'after'): Promise<void> {
    this.window.update((w) => (side === 'before' ? growBefore(w) : growAfter(w)));
    await this.load();
  }
}
