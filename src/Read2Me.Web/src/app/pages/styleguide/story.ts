import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, TemplateRef, input } from '@angular/core';

export type Scheme = 'light' | 'dark';

/**
 * One story on the style guide: a titled block whose content is rendered once per scheme
 * (styles.scss re-emits the theme under `[data-scheme]`), so a component is reviewed in light and
 * dark side by side without flipping the whole app.
 */
@Component({
  selector: 'app-story',
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'story', '[attr.id]': 'anchor()' },
  template: `
    <h2 class="story__title">
      <code class="story__selector">{{ selector() }}</code>
      <span class="story__name">{{ name() }}</span>
    </h2>
    @if (note()) {
      <p class="story__note">{{ note() }}</p>
    }
    <div class="story__panels">
      @for (scheme of schemes(); track scheme) {
        <section
          class="story__panel"
          [attr.data-scheme]="scheme"
          [attr.aria-label]="scheme + ' scheme'"
        >
          <span class="story__scheme">{{ scheme }}</span>
          <div class="story__body"><ng-container *ngTemplateOutlet="body()" /></div>
        </section>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      margin: var(--r2m-space-8) 0;
      scroll-margin-top: var(--r2m-space-4);
    }
    .story__title {
      display: flex;
      align-items: baseline;
      gap: var(--r2m-space-3);
      margin: 0 0 var(--r2m-space-2);
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .story__selector {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-accent);
    }
    .story__note {
      margin: 0 0 var(--r2m-space-3);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .story__panels {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr));
      gap: var(--r2m-space-3);
    }
    .story__panel {
      position: relative;
      padding: var(--r2m-space-6) var(--r2m-space-4) var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--mat-sys-surface);
      color: var(--mat-sys-on-surface);
    }
    .story__scheme {
      position: absolute;
      top: var(--r2m-space-2);
      right: var(--r2m-space-3);
      font-size: var(--r2m-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.08em;
      color: var(--r2m-text-muted);
    }
    .story__body {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-3);
    }
  `,
})
export class Story {
  readonly name = input.required<string>();
  readonly selector = input('');
  readonly note = input<string>();
  readonly anchor = input<string>();
  readonly body = input.required<TemplateRef<unknown>>();
  readonly schemes = input<readonly Scheme[]>(['light', 'dark']);
}
