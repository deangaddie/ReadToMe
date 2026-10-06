import { HeightIndex } from './height-index';

describe('HeightIndex', () => {
  it('estimates unmeasured rows', () => {
    const index = new HeightIndex(50);
    index.setKeys(['a', 'b', 'c']);
    expect(index.total()).toBe(150);
    expect(index.offsetOf(2)).toBe(100);
  });

  it('measured heights replace the estimate and report whether anything changed', () => {
    const index = new HeightIndex(50);
    index.setKeys(['a', 'b', 'c']);
    index.setHeight('a', 50);
    index.setHeight('c', 50);
    expect(index.setHeight('b', 120)).toBe(true);
    expect(index.setHeight('b', 120)).toBe(false);
    expect(index.offsetOf(2)).toBe(170);
    expect(index.total()).toBe(220);
  });

  it('indexAt finds the row covering an offset, clamped to the ends', () => {
    const index = new HeightIndex(10);
    index.setKeys(['a', 'b', 'c']);
    index.setHeight('a', 10);
    index.setHeight('b', 30);
    index.setHeight('c', 10);
    expect(index.indexAt(-5)).toBe(0);
    expect(index.indexAt(0)).toBe(0);
    expect(index.indexAt(9.9)).toBe(0);
    expect(index.indexAt(10)).toBe(1);
    expect(index.indexAt(39)).toBe(1);
    expect(index.indexAt(40)).toBe(2);
    expect(index.indexAt(1000)).toBe(2);
  });

  it('keeps measurements by key when rows are prepended, and offsets shift accordingly', () => {
    const index = new HeightIndex(10);
    index.setKeys(['a', 'b']);
    index.setHeight('a', 40);
    index.setHeight('b', 20);
    index.setKeys(['x', 'y', 'a', 'b']);
    index.setHeight('x', 10);
    index.setHeight('y', 10);
    expect(index.indexOf('a')).toBe(2);
    expect(index.offsetOf(2)).toBe(20);
    expect(index.offsetOf(3)).toBe(60);
  });

  it('an empty index has zero size', () => {
    const index = new HeightIndex(10);
    expect(index.total()).toBe(0);
    expect(index.indexAt(50)).toBe(0);
    expect(index.indexOf('nope')).toBe(-1);
  });

  it('the estimate follows the measured average', () => {
    const index = new HeightIndex(10);
    index.setKeys(['a', 'b', 'c', 'd']);
    index.setHeight('a', 100);
    index.setHeight('b', 50);
    expect(index.total()).toBe(100 + 50 + 75 + 75);
  });
});
