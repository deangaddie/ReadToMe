import type { RouteDef } from '@app/core/router';
import { use } from '@app/core/services';
import { ProjectTitles } from '@app/shell/project-titles';

/**
 * Information architecture from design §4, the Angular `app.routes.ts` in the native router's shape.
 * Every leaf names its element and lazily loads its chunk; a level with a title and no element
 * (`projects`, `settings`) groups its children and gives the breadcrumb its first segment. Until a
 * screen is ported its route renders the placeholder, which names the screen and links to the
 * same path in the Angular app. `guardUnsaved` replaces `canDeactivate: [unsavedChangesGuard]`.
 */
const placeholder = () => import('@app/pages/placeholder-page');
const PLACEHOLDER = 'r2m-placeholder-page';

/** The project crumb and tab title read the loaded project title, the folder name until then. */
const projectTitle = (params: Record<string, string>): string => {
  const folder = params['folder'] ?? '';
  return use(ProjectTitles).titles()[folder] ?? folder;
};

const settingsPage = (path: string, title: string, guardUnsaved = true): RouteDef => ({
  path,
  title,
  tag: PLACEHOLDER,
  load: placeholder,
  guardUnsaved,
});

export const routes: RouteDef[] = [
  { path: '', redirectTo: 'projects' },
  {
    path: 'projects',
    title: 'Projects',
    children: [
      { path: '', tag: PLACEHOLDER, load: placeholder },
      {
        path: ':folder',
        title: projectTitle,
        // The project shell loads the project and holds its hub group for every child route.
        tag: 'r2m-project-shell',
        load: () => import('@app/pages/project/project-shell'),
        children: [
          { path: '', title: 'Overview', tag: PLACEHOLDER, load: placeholder },
          {
            path: 'book',
            title: 'Book',
            tag: 'r2m-book-page',
            load: () => import('@app/pages/book/book-page'),
          },
          { path: 'cast', title: 'Cast', tag: PLACEHOLDER, load: placeholder },
          { path: 'cast/:characterId', title: 'Cast', tag: PLACEHOLDER, load: placeholder },
          {
            path: 'voices/:voiceId/editor',
            title: 'Voice editor',
            tag: PLACEHOLDER,
            load: placeholder,
          },
          { path: 'export', title: 'Export', tag: PLACEHOLDER, load: placeholder },
        ],
      },
    ],
  },
  {
    path: 'settings',
    title: 'Settings',
    children: [
      { path: '', redirectTo: 'settings/llm' },
      settingsPage('llm', 'LLM'),
      settingsPage('prompts', 'Prompts'),
      settingsPage('tts', 'Paragraph TTS'),
      settingsPage('voice-design', 'Voice design'),
      settingsPage('transcription', 'Transcription'),
      settingsPage('similarity', 'Similarity'),
      settingsPage('audio', 'Audio processing'),
      settingsPage('services', 'AI services', false),
      settingsPage('themes', 'Themes', false),
    ],
  },
  { path: '*', title: 'Not found', tag: PLACEHOLDER, load: placeholder },
];
