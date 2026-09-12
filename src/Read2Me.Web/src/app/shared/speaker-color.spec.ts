import { speakerColor, speakerHue } from './speaker-color';

describe('speakerHue', () => {
  it('is deterministic for the same id', () => {
    const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
    expect(speakerHue(id)).toBe(speakerHue(id));
  });

  it('ignores case and surrounding whitespace so the same GUID never gets two colours', () => {
    expect(speakerHue(' ABCDEF ')).toBe(speakerHue('abcdef'));
  });

  it('stays within 0..359', () => {
    for (let i = 0; i < 500; i++) {
      const hue = speakerHue(`character-${i}`);
      expect(hue).toBeGreaterThanOrEqual(0);
      expect(hue).toBeLessThan(360);
    }
  });

  it('spreads nearby ids across the circle rather than clustering them', () => {
    const hues = new Set(Array.from({ length: 50 }, (_, i) => speakerHue(`speaker-${i}`)));
    expect(hues.size).toBeGreaterThan(35);
  });
});

describe('speakerColor', () => {
  it('uses fixed saturation and lightness per scheme, varying only the hue', () => {
    const light = speakerColor('hardin', 'light');
    const dark = speakerColor('hardin', 'dark');
    expect(light.hue).toBe(dark.hue);
    expect(light.foreground).toBe(`hsl(${light.hue} 55% 36%)`);
    expect(light.background).toBe(`hsl(${light.hue} 55% 93%)`);
    expect(dark.foreground).toBe(`hsl(${dark.hue} 60% 74%)`);
    expect(dark.background).toBe(`hsl(${dark.hue} 60% 20%)`);
  });
});
