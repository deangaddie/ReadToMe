import { Routes } from '@angular/router';
import { routeMeta } from './route-meta';
import { unsavedChangesGuard } from './shared/unsaved-changes.guard';

/**
 * Information architecture from design §4. Every leaf is lazy; until its slice lands it resolves
 * to the shared placeholder page. `data` is inherited by children (see app.config.ts), so a project
 * child route sees `section: 'project'` and the `folder` param without redeclaring them.
 */
const placeholder = () =>
  import('./pages/placeholder/placeholder-page').then((m) => m.PlaceholderPage);

/** One page serves the four provider areas (ticket 22); each route's `providerArea` data says which. */
const providerSettingsPage = () =>
  import('./pages/settings/providers/provider-settings-page').then((m) => m.ProviderSettingsPage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'projects' },
  {
    path: 'projects',
    loadComponent: () => import('./pages/projects/projects-page').then((m) => m.ProjectsPage),
    data: routeMeta({ section: 'projects', title: 'Projects' }),
  },
  {
    path: 'projects/:folder',
    // The project shell loads the project and holds its hub group for every child route.
    loadComponent: () => import('./pages/project/project-shell').then((m) => m.ProjectShell),
    data: routeMeta({ section: 'project', title: 'Overview' }),
    children: [
      {
        path: '',
        pathMatch: 'full',
        loadComponent: () => import('./pages/project/overview-page').then((m) => m.OverviewPage),
      },
      {
        path: 'book',
        loadComponent: () => import('./pages/book/book-page').then((m) => m.BookPage),
        data: routeMeta({ section: 'project', title: 'Book' }),
      },
      {
        path: 'cast',
        loadComponent: () => import('./pages/cast/cast-page').then((m) => m.CastPage),
        data: routeMeta({ section: 'project', title: 'Cast' }),
      },
      {
        path: 'cast/:characterId',
        loadComponent: () => import('./pages/cast/cast-page').then((m) => m.CastPage),
        data: routeMeta({ section: 'project', title: 'Cast' }),
      },
      {
        path: 'voices/:voiceId/editor',
        loadComponent: () =>
          import('./pages/voice-editor/voice-editor-page').then((m) => m.VoiceEditorPage),
        data: routeMeta({ section: 'project', title: 'Voice editor' }),
      },
      {
        path: 'export',
        loadComponent: () => import('./pages/export/export-page').then((m) => m.ExportPage),
        data: routeMeta({ section: 'project', title: 'Export' }),
      },
    ],
  },
  {
    path: 'settings',
    data: routeMeta({ section: 'settings', title: 'Settings' }),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'llm' },
      {
        path: 'llm',
        loadComponent: () =>
          import('./pages/settings/llm/llm-settings-page').then((m) => m.LlmSettingsPage),
        canDeactivate: [unsavedChangesGuard],
        data: routeMeta({ section: 'settings', title: 'LLM' }),
      },
      {
        path: 'prompts',
        loadComponent: () =>
          import('./pages/settings/prompts/prompts-page').then((m) => m.PromptsPage),
        canDeactivate: [unsavedChangesGuard],
        data: routeMeta({ section: 'settings', title: 'Prompts' }),
      },
      {
        path: 'tts',
        loadComponent: providerSettingsPage,
        canDeactivate: [unsavedChangesGuard],
        data: {
          ...routeMeta({ section: 'settings', title: 'Paragraph TTS' }),
          providerArea: 'paragraph-tts',
        },
      },
      {
        path: 'voice-design',
        loadComponent: providerSettingsPage,
        canDeactivate: [unsavedChangesGuard],
        data: {
          ...routeMeta({ section: 'settings', title: 'Voice design' }),
          providerArea: 'voice-design',
        },
      },
      {
        path: 'transcription',
        loadComponent: providerSettingsPage,
        canDeactivate: [unsavedChangesGuard],
        data: {
          ...routeMeta({ section: 'settings', title: 'Transcription' }),
          providerArea: 'transcription',
        },
      },
      {
        path: 'similarity',
        loadComponent: providerSettingsPage,
        canDeactivate: [unsavedChangesGuard],
        data: {
          ...routeMeta({ section: 'settings', title: 'Similarity' }),
          providerArea: 'semantic-similarity',
        },
      },
      {
        path: 'audio',
        loadComponent: () =>
          import('./pages/settings/audio/audio-settings-page').then((m) => m.AudioSettingsPage),
        canDeactivate: [unsavedChangesGuard],
        data: routeMeta({ section: 'settings', title: 'Audio processing' }),
      },
      {
        path: 'services',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'AI services', slice: 25 }),
      },
      {
        path: 'themes',
        loadComponent: () =>
          import('./pages/settings/themes/themes-page').then((m) => m.ThemesPage),
        data: routeMeta({ section: 'settings', title: 'Themes' }),
      },
    ],
  },
  {
    path: 'styleguide',
    loadComponent: () => import('./pages/styleguide/styleguide-page').then((m) => m.StyleguidePage),
    data: routeMeta({ section: 'styleguide', title: 'Style guide' }),
  },
  {
    path: '**',
    loadComponent: placeholder,
    data: routeMeta({ section: 'projects', title: 'Not found' }),
  },
];
