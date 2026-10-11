import type { IconName } from '@app/ui/icons';
import type { ShellContext } from '@app/route-meta';

export interface NavItem {
  label: string;
  icon: IconName;
  /** App-relative link (`projects/dune/book`), resolved against `<base href>` like every link. */
  link: string;
  /** `true` matches the link exactly (the project overview must not stay active on /book). */
  exact?: boolean;
}

/** Context group at the top of the rail: what "here" offers (design §5). */
export function contextNavItems(ctx: ShellContext): NavItem[] {
  switch (ctx.section) {
    case 'project': {
      if (!ctx.folder) return [];
      const base = `projects/${encodeURIComponent(ctx.folder)}`;
      return [
        { label: 'Overview', icon: 'dashboard', link: base, exact: true },
        { label: 'Book', icon: 'menu_book', link: `${base}/book` },
        { label: 'Cast', icon: 'groups', link: `${base}/cast` },
        { label: 'Export', icon: 'download', link: `${base}/export` },
      ];
    }
    case 'settings':
      return [
        { label: 'LLM', icon: 'psychology', link: 'settings/llm' },
        { label: 'Prompts', icon: 'description', link: 'settings/prompts' },
        { label: 'Paragraph TTS', icon: 'record_voice_over', link: 'settings/tts' },
        { label: 'Voice design', icon: 'graphic_eq', link: 'settings/voice-design' },
        { label: 'Transcription', icon: 'subtitles', link: 'settings/transcription' },
        { label: 'Similarity', icon: 'compare', link: 'settings/similarity' },
        { label: 'Audio processing', icon: 'equalizer', link: 'settings/audio' },
        { label: 'AI services', icon: 'dns', link: 'settings/services' },
        { label: 'Themes', icon: 'palette', link: 'settings/themes' },
      ];
    default:
      return [];
  }
}

/** Global group at the bottom of the rail. The style guide is pruned (spec §8.2). */
export const GLOBAL_NAV_ITEMS: readonly NavItem[] = [
  { label: 'Projects', icon: 'library_books', link: 'projects', exact: true },
  { label: 'Settings', icon: 'settings', link: 'settings' },
];

/** `routerLinkActive`: the item's link is the current path, or (unless exact) a prefix of it. */
export function isActive(item: NavItem, path: string): boolean {
  if (path === item.link) return true;
  return !item.exact && path.startsWith(`${item.link}/`);
}
