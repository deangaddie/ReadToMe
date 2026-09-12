import { Routes } from '@angular/router';
import { routeMeta } from './route-meta';

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
    loadComponent: placeholder,
    data: routeMeta({ section: 'projects', title: 'Projects', slice: 8 }),
  },
  {
    path: 'projects/:folder',
    data: routeMeta({ section: 'project', title: 'Overview', slice: 9 }),
    children: [
      { path: '', pathMatch: 'full', loadComponent: placeholder },
      {
        path: 'book',
        loadComponent: placeholder,
        data: routeMeta({ section: 'project', title: 'Book', slice: 10 }),
      },
      {
        path: 'cast',
        loadComponent: placeholder,
        data: routeMeta({ section: 'project', title: 'Cast', slice: 15 }),
      },
      {
        path: 'cast/:characterId',
        loadComponent: placeholder,
        data: routeMeta({ section: 'project', title: 'Cast', slice: 15 }),
      },
      {
        path: 'voices/:voiceId/editor',
        loadComponent: placeholder,
        data: routeMeta({ section: 'project', title: 'Voice editor', slice: 18 }),
      },
      {
        path: 'export',
        loadComponent: placeholder,
        data: routeMeta({ section: 'project', title: 'Export', slice: 20 }),
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
        loadComponent: placeholder,
        data: routeMeta({ section: 'settings', title: 'LLM', slice: 21 }),
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
