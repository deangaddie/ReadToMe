import { describe, expect, it } from 'bun:test';
import { pronunciationsDock } from './pronunciation-dock';

describe('pronunciationsDock', () => {
  describe('the tree at 280 px: the boundary the 1100 px container query had', () => {
    it('docks on a Book body wider than 1100 px', () => {
      expect(pronunciationsDock(1101, 280)).toBe('docked');
      expect(pronunciationsDock(1400, 280)).toBe('docked');
    });

    it('floats on a Book body of 1100 px or less', () => {
      expect(pronunciationsDock(1100, 280)).toBe('floating');
      expect(pronunciationsDock(900, 280)).toBe('floating');
    });
  });

  describe('a wider tree moves the boundary by its extra width', () => {
    it('floats up to 1220 px with a 400 px tree', () => {
      expect(pronunciationsDock(1101, 400)).toBe('floating');
      expect(pronunciationsDock(1220, 400)).toBe('floating');
    });

    it('docks past 1220 px with a 400 px tree', () => {
      expect(pronunciationsDock(1221, 400)).toBe('docked');
    });
  });

  describe('the tree hidden: the 860 px breakpoint', () => {
    it('docks on a Book body wider than 860 px', () => {
      expect(pronunciationsDock(861, null)).toBe('docked');
    });

    it('floats on a Book body of 860 px or less', () => {
      expect(pronunciationsDock(860, null)).toBe('floating');
      expect(pronunciationsDock(500, null)).toBe('floating');
    });
  });
});
