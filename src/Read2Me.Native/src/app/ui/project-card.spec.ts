import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { ProjectSummary } from '@app/api';
import { placeholderGradient, projectCard, projectInitials } from './project-card';

const base: ProjectSummary = {
  folderName: 'foundation',
  title: 'Foundation',
  author: 'Isaac Asimov',
  coverImage: null,
  audioItemTotal: 0,
  audioItemDone: 0,
  audioPercent: 0,
  fileType: 'Epub',
};

afterEach(() => document.body.replaceChildren());

function mount(project: ProjectSummary = base) {
  const host = document.createElement('div');
  document.body.append(host);
  const counts = { opened: 0, deleted: 0 };
  render(
    projectCard(project, { onOpen: () => counts.opened++, onDelete: () => counts.deleted++ }),
    host,
  );
  return { el: host, counts };
}

describe('projectCard()', () => {
  it('shows placeholder initials and "Not read in yet" when there is no cover or audio', () => {
    const { el } = mount();
    expect(el.querySelector('.r2m-project-card')?.getAttribute('data-folder')).toBe('foundation');
    expect(el.querySelector('.r2m-project-card__image')).toBeNull();
    expect(el.querySelector('.r2m-project-card__initials')?.textContent).toBe('F');
    expect(el.querySelector('.r2m-project-card__unread')?.textContent).toBe('Not read in yet');
    expect(el.textContent).toContain('Isaac Asimov');
  });

  it('shows the workspace cover and the audio progress bar', () => {
    const { el } = mount({
      ...base,
      coverImage: 'cover.png',
      audioItemTotal: 40,
      audioItemDone: 10,
      audioPercent: 25,
    });
    const img = el.querySelector<HTMLImageElement>('.r2m-project-card__image')!;
    expect(img.getAttribute('src')).toBe('/workspace/foundation/cover.png');
    const bar = el.querySelector('.r2m-project-card__progress')!;
    expect(bar.getAttribute('aria-valuenow')).toBe('25');
    expect(el.querySelector<HTMLElement>('.r2m-project-card__fill')!.style.width).toBe('25%');
    expect(el.querySelector('.r2m-project-card__unread')).toBeNull();
  });

  it('calls onOpen from the cover and title, onDelete from the overflow menu', () => {
    const { el, counts } = mount();
    el.querySelector<HTMLButtonElement>('.r2m-project-card__cover')!.click();
    el.querySelector<HTMLButtonElement>('.r2m-project-card__title')!.click();
    expect(counts.opened).toBe(2);

    const more = el.querySelector<HTMLButtonElement>('.r2m-project-card__more')!;
    const menu = el.querySelector<HTMLElement>('[role=menu]')!;
    expect(more.getAttribute('popovertarget')).toBe(menu.id);
    expect(more.getAttribute('aria-label')).toBe('Actions for Foundation');
    menu.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    expect(counts.deleted).toBe(1);
  });

  it('derives initials and a stable gradient', () => {
    expect(projectInitials('the left hand of darkness')).toBe('TL');
    expect(projectInitials('  Dune ')).toBe('D');
    expect(projectInitials('Magician (shelf test)')).toBe('MS');
    expect(projectInitials('1984')).toBe('1');
    expect(placeholderGradient('abc')).toBe(placeholderGradient('abc'));
    expect(placeholderGradient('abc')).not.toBe(placeholderGradient('abd'));
  });
});
