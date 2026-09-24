import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';
import { ActivityStore, ActivityTab } from './activity-store';
import { JobsTab } from './jobs-tab';
import { ServicesTab } from './services-tab';
import { AudioStreamTab, LlmStreamTab } from './stream-tabs';

export const ACTIVITY_TABS: { id: ActivityTab; label: string; icon: string }[] = [
  { id: 'jobs', label: 'Jobs', icon: 'pending_actions' },
  { id: 'llm', label: 'LLM', icon: 'psychology' },
  { id: 'audio', label: 'Audio', icon: 'graphic_eq' },
  { id: 'services', label: 'Services', icon: 'dns' },
];

/**
 * The activity drawer's content (design §5): Jobs · LLM · Audio · Services. Only the selected
 * tab's component exists, so a stream tab's hub group is joined exactly while it is visible. The
 * shell mounts this only while the drawer is open for the same reason.
 */
@Component({
  selector: 'app-activity-drawer',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatTabsModule,
    JobsTab,
    LlmStreamTab,
    AudioStreamTab,
    ServicesTab,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-drawer' },
  template: `
    <div class="activity-drawer__head">
      <span class="activity-drawer__title">Activity</span>
      <button
        mat-icon-button
        type="button"
        (click)="store.closeDrawer()"
        aria-label="Close activity drawer"
      >
        <mat-icon>close</mat-icon>
      </button>
    </div>
    <nav
      mat-tab-nav-bar
      mat-stretch-tabs
      class="activity-drawer__tabs"
      [tabPanel]="panel"
      [disablePagination]="true"
      aria-label="Activity sections"
    >
      @for (tab of tabs; track tab.id) {
        <a
          mat-tab-link
          [active]="store.tab() === tab.id"
          [attr.data-tab]="tab.id"
          (click)="store.tab.set(tab.id)"
          (keydown.enter)="store.tab.set(tab.id)"
          tabindex="0"
        >
          <mat-icon class="activity-drawer__tab-icon" aria-hidden="true">{{ tab.icon }}</mat-icon>
          <span class="activity-drawer__tab-label">{{ tab.label }}</span>
        </a>
      }
    </nav>
    <mat-tab-nav-panel #panel class="activity-drawer__body">
      @switch (store.tab()) {
        @case ('jobs') {
          <app-jobs-tab />
        }
        @case ('llm') {
          <app-llm-stream-tab />
        }
        @case ('audio') {
          <app-audio-stream-tab />
        }
        @case ('services') {
          <app-services-tab />
        }
      }
    </mat-tab-nav-panel>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .activity-drawer__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex: 0 0 var(--r2m-appbar-height);
      padding: 0 var(--r2m-space-2) 0 var(--r2m-space-4);
      border-bottom: 1px solid var(--r2m-outline);
    }
    .activity-drawer__title {
      font-weight: 600;
    }
    .activity-drawer__tabs {
      flex: 0 0 auto;
      border-bottom: 1px solid var(--r2m-outline);
    }
    /* Four icon+label tabs must fit the 420 px drawer without pagination. */
    .activity-drawer__tabs ::ng-deep .mat-mdc-tab-link {
      min-width: 0;
      padding: 0 var(--r2m-space-2);
    }
    .activity-drawer__tab-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
      margin-right: var(--r2m-space-1);
    }
    .activity-drawer__body {
      display: block;
      flex: 1 1 auto;
      min-height: 0;
    }
  `,
})
export class ActivityDrawer {
  protected readonly store = inject(ActivityStore);
  protected readonly tabs = ACTIVITY_TABS;
}
