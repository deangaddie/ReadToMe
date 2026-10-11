import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { AiServicesStore } from '@app/ai-services/ai-services-store';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { dockerControls } from '@app/ui/docker-controls';
import type { IconName } from '@app/ui/icons';
import { jobCard } from '@app/ui/job-pill';
import { emptyState, icon } from '@app/ui/partials';
import { type TabDef, tabPanel, tabs } from '@app/ui/tabs';
import { throughput } from '@app/ui/throughput';
import { type ActivityTab, ActivityStore } from './activity-store';
import { AudioStreamFeed, LlmStreamFeed } from './stream-feed';
import './styles';
import '@app/ui/stream-llm';
import '@app/ui/stream-audio';

export const ACTIVITY_TABS: TabDef<ActivityTab>[] = [
  { id: 'jobs', label: 'Jobs', icon: 'pending_actions' },
  { id: 'llm', label: 'LLM', icon: 'psychology' },
  { id: 'audio', label: 'Audio', icon: 'graphic_eq' },
  { id: 'services', label: 'Services', icon: 'dns' },
];

const TABS_ID = 'r2m-activity';

/**
 * The activity drawer's content (design §5): Jobs · LLM · Audio · Services. Only the selected
 * tab's content exists, so a stream tab's hub group is joined exactly while it is visible. The
 * shell mounts this only while the drawer is open for the same reason.
 */
export class ActivityDrawer extends R2mElement {
  private readonly store = use(ActivityStore);

  protected override connected(): void {
    this.classList.add('activity-drawer');
  }

  protected template() {
    const tab = this.store.tab();
    return html`
      <div class="activity-drawer__head">
        <span class="activity-drawer__title">Activity</span>
        <button
          type="button"
          class="r2m-icon-button"
          aria-label="Close activity drawer"
          @click=${() => this.store.closeDrawer()}
        >
          ${icon('close')}
        </button>
      </div>
      ${tabs({
        id: TABS_ID,
        tabs: ACTIVITY_TABS,
        selected: tab,
        label: 'Activity sections',
        onSelect: (next) => this.store.tab.set(next),
      })}
      ${tabPanel(TABS_ID, tab, this.body(tab), 'activity-drawer__body')}
    `;
  }

  private body(tab: ActivityTab) {
    switch (tab) {
      case 'jobs':
        return jobsTab(this.store);
      case 'llm':
        return html`<r2m-llm-stream-tab></r2m-llm-stream-tab>`;
      case 'audio':
        return html`<r2m-audio-stream-tab></r2m-audio-stream-tab>`;
      case 'services':
        return html`<r2m-services-tab></r2m-services-tab>`;
    }
  }
}
define('r2m-activity-drawer', ActivityDrawer);

/**
 * The drawer's Jobs tab (ticket 14): one job card per job with Cancel / Dismiss, then the LLM
 * throughput block — headline and sparkline while a run is active, the per-config table once it
 * has ended, and Dismiss to retire it. Stateless, so a partial.
 */
export function jobsTab(store: ActivityStore) {
  const jobs = store.jobs();
  const t = store.throughput();
  const showThroughput = store.showThroughput() && t;
  return html`<div class="activity-jobs">
    ${
      jobs.length === 0 && !showThroughput
        ? emptyState({
            compact: true,
            icon: 'pending_actions',
            headline: 'No background work',
            hint: 'Attribution, audio generation, voice batches and assembly show up here while they run.',
          })
        : nothing
    }
    ${repeat(
      jobs,
      (job) => job.id,
      (job) =>
        jobCard({
          job,
          onCancel: (id) => void store.cancel(id),
          onDismiss: (id) => store.dismiss(id),
        }),
    )}
    ${
      showThroughput
        ? html`<section class="activity-jobs__throughput" aria-label="LLM throughput">
            <header class="activity-jobs__throughput-head">
              ${icon('speed')}
              <span class="activity-jobs__throughput-title">LLM throughput</span>
            </header>
            ${throughput(t)}
            ${
              t.isRunActive
                ? nothing
                : html`<footer class="activity-jobs__throughput-actions">
                    <button
                      type="button"
                      class="r2m-button"
                      @click=${() => void store.dismissThroughput()}
                    >
                      Dismiss
                    </button>
                  </footer>`
            }
          </section>`
        : nothing
    }
  </div>`;
}

/**
 * The drawer's LLM tab (ticket 14, design §9): exists only while the tab is visible, and its
 * lifetime is the feed's reference — connect joins `stream:llm`, disconnect leaves it.
 */
export class LlmStreamTab extends R2mElement {
  private readonly feed = use(LlmStreamFeed);

  protected override connected(): void {
    this.classList.add('activity-stream-tab');
    this.feed.acquire();
    this.onDisconnect(() => this.feed.release());
  }

  protected template() {
    return html`<r2m-stream-llm
      .events=${this.feed.events()}
      .maxTurns=${this.feed.maxUnits}
    ></r2m-stream-llm>`;
  }
}
define('r2m-llm-stream-tab', LlmStreamTab);

/** The drawer's Audio tab: same join-while-visible rule over `stream:audio`. */
export class AudioStreamTab extends R2mElement {
  private readonly feed = use(AudioStreamFeed);

  protected override connected(): void {
    this.classList.add('activity-stream-tab');
    this.feed.acquire();
    this.onDisconnect(() => this.feed.release());
  }

  protected template() {
    return html`<r2m-stream-audio
      .events=${this.feed.events()}
      .maxCards=${this.feed.maxUnits}
    ></r2m-stream-audio>`;
  }
}
define('r2m-audio-stream-tab', AudioStreamTab);

const SERVICE_ICON = (usesGpu: boolean): IconName => (usesGpu ? 'memory' : 'dns');

/**
 * The drawer's Services tab (tickets 14, 25): the compact form of `/settings/services` — one row
 * per managed container with the same hub-fed status chip and Start / Restart / Shutdown / Refresh,
 * all through the app-wide {@link AiServicesStore}. An element because it loads on connect.
 */
export class ServicesTab extends R2mElement {
  private readonly store = use(AiServicesStore);

  protected override connected(): void {
    this.classList.add('activity-services');
    void this.store.ensureLoaded();
  }

  protected template() {
    const error = this.store.error();
    const services = this.store.services();
    return html`
      ${
        error
          ? emptyState({
              compact: true,
              icon: 'cloud_off',
              headline: 'Services unavailable',
              hint: error,
            })
          : services === null
            ? html`<p class="activity-services__loading">Loading services…</p>`
            : services.length === 0
              ? emptyState({ compact: true, icon: 'dns', headline: 'No managed services' })
              : html`<ul class="activity-services__list">
                  ${repeat(
                    services,
                    (svc) => svc.name,
                    (svc) =>
                      html`<li class="activity-services__row" data-service=${svc.name}>
                        ${icon(SERVICE_ICON(svc.usesGpu), 'activity-services__icon')}
                        <span class="activity-services__name">
                          <span>${svc.name}</span>
                          <span class="activity-services__container">${svc.containerName}</span>
                        </span>
                        ${dockerControls({
                          status: this.store.statusOf(svc.name),
                          busy: this.store.isBusy(svc.name),
                          onStart: () => void this.store.start(svc.name),
                          onRestart: () => void this.store.restart(svc.name),
                          onShutdown: () => void this.store.shutdown(svc.name),
                          onRefresh: () => void this.store.refresh(svc.name),
                        })}
                      </li>`,
                  )}
                </ul>`
      }
      <div class="activity-services__foot">
        <a class="r2m-button r2m-button--stroked" href="settings/services">
          ${icon('settings')}Manage services
        </a>
      </div>
    `;
  }
}
define('r2m-services-tab', ServicesTab);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-activity-drawer': ActivityDrawer;
    'r2m-llm-stream-tab': LlmStreamTab;
    'r2m-audio-stream-tab': AudioStreamTab;
    'r2m-services-tab': ServicesTab;
  }
}
