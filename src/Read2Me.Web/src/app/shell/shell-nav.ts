import { RouteMeta, Section } from '@app/route-meta';

export interface NavItem {
  label: string;
  icon: string;
  link: string[];
  /** `true` matches the link exactly (the project overview must not stay active on /book). */
  exact?: boolean;
}

export interface Crumb {
  label: string;
  link?: string[];
}

export interface ShellContext {
  section: Section;
  title: string;
  folder?: string;
}

/** Context group at the top of the rail: what "here" offers (design §5). */
export function contextNavItems(ctx: ShellContext): NavItem[] {
  switch (ctx.section) {
    case 'project': {
      if (!ctx.folder) return [];
      const base = ['/projects', ctx.folder];
      return [
        { label: 'Overview', icon: 'dashboard', link: base, exact: true },
        { label: 'Book', icon: 'menu_book', link: [...base, 'book'] },
        { label: 'Cast', icon: 'groups', link: [...base, 'cast'] },
        { label: 'Export', icon: 'download', link: [...base, 'export'] },
      ];
    }
    case 'settings':
      return [
        { label: 'LLM', icon: 'psychology', link: ['/settings', 'llm'] },
        { label: 'Prompts', icon: 'description', link: ['/settings', 'prompts'] },
        { label: 'Paragraph TTS', icon: 'record_voice_over', link: ['/settings', 'tts'] },
        { label: 'Voice design', icon: 'graphic_eq', link: ['/settings', 'voice-design'] },
        { label: 'Transcription', icon: 'subtitles', link: ['/settings', 'transcription'] },
        { label: 'Similarity', icon: 'compare', link: ['/settings', 'similarity'] },
        { label: 'Audio processing', icon: 'equalizer', link: ['/settings', 'audio'] },
        { label: 'AI services', icon: 'dns', link: ['/settings', 'services'] },
        { label: 'Themes', icon: 'palette', link: ['/settings', 'themes'] },
      ];
    default:
      return [];
  }
}

/** Global group at the bottom of the rail. */
export const GLOBAL_NAV_ITEMS: readonly NavItem[] = [
  { label: 'Projects', icon: 'library_books', link: ['/projects'], exact: true },
  { label: 'Settings', icon: 'settings', link: ['/settings'] },
  { label: 'Style guide', icon: 'style', link: ['/styleguide'] },
];

/**
 * Breadcrumb for the app bar: project › section, or settings › page. The project crumb shows
 * `projectTitle` once the project shell has loaded it, the folder name before.
 */
export function breadcrumb(ctx: ShellContext, projectTitle?: string): Crumb[] {
  switch (ctx.section) {
    case 'project':
      return ctx.folder
        ? [
            { label: 'Projects', link: ['/projects'] },
            { label: projectTitle || ctx.folder, link: ['/projects', ctx.folder] },
            ...(ctx.title === 'Overview' ? [] : [{ label: ctx.title }]),
          ]
        : [{ label: 'Projects', link: ['/projects'] }];
    case 'settings':
      return [{ label: 'Settings', link: ['/settings'] }, { label: ctx.title }];
    case 'styleguide':
      return [{ label: 'Style guide' }];
    default:
      return [{ label: ctx.title }];
  }
}

export function toShellContext(
  meta: RouteMeta,
  params: Record<string, string | undefined>,
): ShellContext {
  const folder = params['folder'];
  return folder === undefined
    ? { section: meta.section, title: meta.title }
    : { section: meta.section, title: meta.title, folder };
}
