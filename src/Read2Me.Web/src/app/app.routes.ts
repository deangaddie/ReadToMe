import type { RouteDef } from '@app/core/router';
import { use } from '@app/core/services';
import { PROVIDER_PAGE_TAGS } from '@app/pages/settings/providers/provider-area';
import { ProjectTitles } from '@app/shell/project-titles';

/** The project crumb and tab title read the loaded project title, the folder name until then. */
const projectTitle = (params: Record<string, string>): string => {
  const folder = params['folder'] ?? '';
  return use(ProjectTitles).titles()[folder] ?? folder;
};

/** One of the four provider areas, whose page element `PROVIDER_PAGE_TAGS` names. */
const providerPage = (path: string, title: string, tag: string): RouteDef => ({
  path,
  title,
  tag,
  load: () => import('@app/pages/settings/providers/provider-settings-page'),
  guardUnsaved: true,
});

/**
 * Information architecture from design §4. Every leaf names its element and lazily loads its
 * chunk; a level with a title and no element (`projects`, `settings`) groups its children and
 * gives the breadcrumb its first segment. An unknown path gets the not-found page.
 * `guardUnsaved` asks before leaving a page with unsaved edits.
 */
export const routes: RouteDef[] = [
  { path: '', redirectTo: 'projects' },
  {
    path: 'projects',
    title: 'Projects',
    children: [
      {
        path: '',
        tag: 'r2m-projects-page',
        load: () => import('@app/pages/projects/projects-page'),
      },
      {
        path: ':folder',
        title: projectTitle,
        // The project shell loads the project and holds its hub group for every child route.
        tag: 'r2m-project-shell',
        load: () => import('@app/pages/project/project-shell'),
        children: [
          {
            path: '',
            title: 'Overview',
            tag: 'r2m-overview-page',
            load: () => import('@app/pages/project/overview-page'),
          },
          {
            path: 'book',
            title: 'Book',
            tag: 'r2m-book-page',
            load: () => import('@app/pages/book/book-page'),
          },
          {
            path: 'cast',
            title: 'Cast',
            tag: 'r2m-cast-page',
            load: () => import('@app/pages/cast/cast-page'),
          },
          {
            path: 'cast/:characterId',
            title: 'Cast',
            tag: 'r2m-cast-page',
            load: () => import('@app/pages/cast/cast-page'),
          },
          {
            path: 'pronunciations',
            title: 'Pronunciations',
            tag: 'r2m-pronunciations-page',
            load: () => import('@app/pages/pronunciations/pronunciations-page'),
            guardUnsaved: true,
          },
          {
            path: 'voices/:voiceId/editor',
            title: 'Voice editor',
            tag: 'r2m-voice-editor-page',
            load: () => import('@app/pages/voice-editor/voice-editor-page'),
          },
          {
            path: 'export',
            title: 'Export',
            tag: 'r2m-export-page',
            load: () => import('@app/pages/export/export-page'),
          },
        ],
      },
    ],
  },
  {
    path: 'settings',
    title: 'Settings',
    children: [
      { path: '', redirectTo: 'settings/llm' },
      {
        path: 'llm',
        title: 'LLM',
        tag: 'r2m-llm-settings-page',
        load: () => import('@app/pages/settings/llm/llm-settings-page'),
        guardUnsaved: true,
      },
      {
        path: 'prompts',
        title: 'Prompts',
        tag: 'r2m-prompts-page',
        load: () => import('@app/pages/settings/prompts/prompts-page'),
        guardUnsaved: true,
      },
      providerPage('tts', 'Paragraph TTS', PROVIDER_PAGE_TAGS['paragraph-tts']),
      providerPage('voice-design', 'Voice design', PROVIDER_PAGE_TAGS['voice-design']),
      providerPage('transcription', 'Transcription', PROVIDER_PAGE_TAGS.transcription),
      providerPage('similarity', 'Similarity', PROVIDER_PAGE_TAGS['semantic-similarity']),
      {
        path: 'audio',
        title: 'Audio processing',
        tag: 'r2m-audio-settings-page',
        load: () => import('@app/pages/settings/audio/audio-settings-page'),
        guardUnsaved: true,
      },
      {
        path: 'services',
        title: 'AI services',
        tag: 'r2m-ai-services-page',
        load: () => import('@app/pages/settings/services/ai-services-page'),
      },
      {
        path: 'themes',
        title: 'Themes',
        tag: 'r2m-themes-page',
        load: () => import('@app/pages/settings/themes/themes-page'),
      },
    ],
  },
  {
    path: '*',
    title: 'Not found',
    tag: 'r2m-not-found-page',
    load: () => import('@app/pages/not-found-page'),
  },
];
