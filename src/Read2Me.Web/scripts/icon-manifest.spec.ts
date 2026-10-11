import { describe, expect, it } from 'bun:test';
import { manifestProblems, sortIconNames, subsetCssUrl, woff2Url } from './icon-manifest';

describe('sortIconNames', () => {
  it('sorts the names and drops repeats, leaving the input alone', () => {
    const union = ['search', 'error', 'search', 'auto_stories'] as const;
    expect(sortIconNames(union)).toEqual(['auto_stories', 'error', 'search']);
    expect(union[0]).toBe('search');
  });
});

describe('manifestProblems', () => {
  it('has nothing to say when the manifest is the sorted union', () => {
    expect(manifestProblems(['search', 'error'], ['error', 'search'])).toEqual([]);
  });

  it('names an icon the union has and the font lacks', () => {
    expect(manifestProblems(['search', 'error', 'link'], ['error', 'search'])).toEqual([
      'in IconName but not in the font: link',
    ]);
  });

  it('names an icon the font still carries after the union dropped it', () => {
    expect(manifestProblems(['search'], ['error', 'search'])).toEqual([
      'in the font but not in IconName: error',
    ]);
  });

  it('reports both directions of a rename', () => {
    expect(manifestProblems(['delete_forever'], ['delete'])).toEqual([
      'in IconName but not in the font: delete_forever',
      'in the font but not in IconName: delete',
    ]);
  });

  it('rejects a manifest with the right names in the wrong order, or one listed twice', () => {
    const unsorted = 'the manifest is not sorted, or lists a name twice';
    expect(manifestProblems(['error', 'search'], ['search', 'error'])).toEqual([unsorted]);
    expect(manifestProblems(['error', 'search'], ['error', 'search', 'search'])).toEqual([
      unsorted,
    ]);
  });

  it('rejects a manifest that is not a list of names', () => {
    const malformed = ['the manifest is not a JSON array of icon names'];
    expect(manifestProblems(['search'], { names: ['search'] })).toEqual(malformed);
    expect(manifestProblems(['search'], ['search', 3])).toEqual(malformed);
    expect(manifestProblems(['search'], null)).toEqual(malformed);
  });
});

describe('subsetCssUrl', () => {
  it('asks for the sorted names with the full range of every axis', () => {
    expect(subsetCssUrl(['search', 'auto_stories'])).toBe(
      'https://fonts.googleapis.com/css2?family=Material+Symbols+Rounded:opsz,wght,FILL,GRAD@20..48,100..700,0..1,-50..200&icon_names=auto_stories,search&display=block',
    );
  });

  it('refuses an empty union and a name that is not a ligature name', () => {
    expect(() => subsetCssUrl([])).toThrow('no icon names');
    expect(() => subsetCssUrl(['search', 'Bad-Name'])).toThrow('Bad-Name');
  });
});

describe('woff2Url', () => {
  it('takes the font URL from the @font-face rule', () => {
    const css = `@font-face {\n  font-family: 'Material Symbols Rounded';\n  src: url(https://fonts.gstatic.com/l/font?kit=abc&v=v1) format('woff2');\n}`;
    expect(woff2Url(css)).toBe('https://fonts.gstatic.com/l/font?kit=abc&v=v1');
  });

  it('fails when the CSS has no woff2, as it does for a client that is not a browser', () => {
    expect(() => woff2Url(`src: url(https://x/font.ttf) format('truetype');`)).toThrow(
      'expected one woff2',
    );
  });
});
