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
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Prompts', slice: 23 }),
      },
      {
        path: 'tts',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Paragraph TTS', slice: 22 }),
      },
      {
        path: 'voice-design',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Voice design', slice: 22 }),
      },
      {
        path: 'transcription',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Transcription', slice: 22 }),
      },
      {
        path: 'similarity',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Similarity', slice: 22 }),
      },
      {
        path: 'audio',
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'Audio processing', slice: 24 }),
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
