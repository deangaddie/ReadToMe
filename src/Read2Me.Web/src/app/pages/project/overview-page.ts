import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { statusChip } from '@app/ui/partials';
import { pipeline } from '@app/ui/pipeline';
import { helpPanel } from './help-panel';
import { type StepId, readOnlyPipeline } from './pipeline-steps';
import { ProjectStore } from './project-store';
import { StepRunner } from './step-runner';
import './project-details-panel';
import overviewCss from './overview-page.css' with { type: 'text' };

adoptStyles(overviewCss);

/**
 * `/projects/{folder}` (design §6.2, guidance-primitives spec §3): the nine-step Pipeline stepper
 * beside the project card. The steps are the {@link ProjectStore}'s `pipeline`; row actions and
 * checkpoint pills go through the shell's {@link StepRunner}, as the next-step pill's do. A row's
 * `?` opens that step's help: in the right column in place of the details card at laptop width,
 * inside the row below 1100 px; both render and the CSS shows one. Reads the store the project
 * shell provides.
 */
export class OverviewPage extends R2mElement {
  private store!: ProjectStore;
  private runner!: StepRunner;

  /** The one step whose help is open, or null. */
  readonly helpOpen = signal<StepId | null>(null);

  protected override connected(): void {
    this.classList.add('overview');
    this.store = use(ProjectStore, this);
    this.runner = use(StepRunner, this);
  }

  protected template() {
    const detail = this.store.detail();
    const error = this.store.error();
    const readOnly = this.store.readOnly();
    const pipelineSteps = this.store.pipeline();
    // Viewing a History step: the rows still open pages, but nothing on them changes the project.
    const steps =
      pipelineSteps && readOnly !== null
        ? readOnlyPipeline(pipelineSteps, readOnly)
        : pipelineSteps;
    const author = detail?.author;
    const helpStep = steps?.find((s) => s.id === this.helpOpen()) ?? null;
    const closeHelp = () => this.helpOpen.set(null);
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">${detail?.title ?? this.store.folder() ?? ''}</h1>
        ${author ? html`<p class="r2m-page-header__subtitle">${author}</p>` : nothing}
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Project could not be loaded: ${error}` })
          : nothing
      }

      <div class="overview__body ${helpStep ? 'overview__body--help-open' : ''}">
        <section class="overview__pipeline" aria-label="Production pipeline">
          ${
            steps
              ? pipeline({
                  steps,
                  busyAction: this.runner.busy(),
                  helpOpen: this.helpOpen(),
                  onAct: (e) => void this.runner.act(e),
                  onCheckpoint: (e) => void this.runner.checkpoint(e),
                  onHelp: (id) => this.helpOpen.set(this.helpOpen() === id ? null : id),
                  renderHelp: (step) => helpPanel(step, closeHelp, 'inline'),
                })
              : this.store.loading()
                ? html`<div
                    class="overview__skeleton"
                    aria-busy="true"
                    aria-label="Loading pipeline"
                  ></div>`
                : nothing
          }
        </section>
        <aside class="overview__details" aria-label="Project">
          ${helpStep ? helpPanel(helpStep, closeHelp, 'side') : nothing}
          <div class="overview__card"><r2m-project-details-panel></r2m-project-details-panel></div>
        </aside>
      </div>
    `;
  }
}
define('r2m-overview-page', OverviewPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-overview-page': OverviewPage;
  }
}
