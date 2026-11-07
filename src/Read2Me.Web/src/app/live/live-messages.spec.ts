import { hasFacet } from './live-messages';

describe('hasFacet', () => {
  it('finds a facet in the list a receipt carries', () => {
    expect(hasFacet(['Structure', 'Audio'], 'Audio')).toBe(true);
    expect(hasFacet(['Structure', 'Audio'], 'Voices')).toBe(false);
    expect(hasFacet(['Attribution'], 'Attribution')).toBe(true);
    expect(hasFacet([], 'Structure')).toBe(false);
  });
});
