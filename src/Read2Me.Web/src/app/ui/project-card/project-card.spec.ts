import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ProjectSummary } from '@app/api';
import { ProjectCard, placeholderGradient, projectInitials } from './project-card';

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

@Component({
  imports: [ProjectCard],
  template: `<r2m-project-card
    [project]="project()"
    (open)="opened = opened + 1"
    (delete)="deleted = deleted + 1"
  />`,
})
class Host {
  readonly project = signal<ProjectSummary>(base);
  opened = 0;
  deleted = 0;
}

describe('r2m-project-card', () => {
  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  function render(project: ProjectSummary = base) {
    TestBed.configureTestingModule({ imports: [Host] });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.project.set(project);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, host: fixture.componentInstance };
  }

  it('shows placeholder initials and "Not read in yet" when there is no cover or audio', () => {
    const { el } = render();
    expect(el.querySelector('.r2m-project-card__image')).toBeNull();
    expect(el.querySelector('.r2m-project-card__initials')?.textContent).toBe('F');
    expect(el.querySelector('.r2m-project-card__unread')?.textContent).toBe('Not read in yet');
    expect(el.textContent).toContain('Isaac Asimov');
  });

  it('shows the workspace cover and the audio progress bar', () => {
    const { el } = render({
      ...base,
      coverImage: 'cover.png',
      audioItemTotal: 40,
      audioItemDone: 10,
      audioPercent: 25,
    });
    const img = el.querySelector('.r2m-project-card__image') as HTMLImageElement;
    expect(img.getAttribute('src')).toBe('/workspace/foundation/cover.png');
    const bar = el.querySelector('.r2m-project-card__progress')!;
    expect(bar.getAttribute('aria-valuenow')).toBe('25');
    expect((el.querySelector('.r2m-project-card__fill') as HTMLElement).style.width).toBe('25%');
    expect(el.querySelector('.r2m-project-card__unread')).toBeNull();
  });

  it('emits open from the cover and title, delete from the overflow menu', async () => {
    const { fixture, el, host } = render();
    (el.querySelector('.r2m-project-card__cover') as HTMLButtonElement).click();
    (el.querySelector('.r2m-project-card__title') as HTMLButtonElement).click();
    expect(host.opened).toBe(2);

    (el.querySelector('.r2m-project-card__more') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    (document.querySelector('.r2m-project-card__delete') as HTMLButtonElement).click();
    expect(host.deleted).toBe(1);
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
