/**
 * Row offsets for a virtual list whose rows have different heights. Heights are remembered by row
 * key, so rows can be prepended or re-ordered without losing measurements; rows not yet measured
 * count as the average of those that are (or the initial estimate before any are).
 */
export class HeightIndex {
  private keys: readonly string[] = [];
  private positions = new Map<string, number>();
  private readonly heights = new Map<string, number>();
  /** Estimates held by unmeasured rows that sit above a measured row. */
  private readonly pinned = new Map<string, number>();
  private measuredSum = 0;
  /** `offsets[i]` = top of row i; `offsets[length]` = total. Rebuilt lazily. */
  private offsets: Float64Array | null = null;

  constructor(private readonly initialEstimate: number) {}

  get length(): number {
    return this.keys.length;
  }

  setKeys(keys: readonly string[]): void {
    this.keys = keys;
    this.positions = new Map(keys.map((k, i) => [k, i]));
    // A row that left the list is estimated afresh if it comes back.
    for (const key of this.pinned.keys()) if (!this.positions.has(key)) this.pinned.delete(key);
    this.offsets = null;
  }

  keyAt(index: number): string | undefined {
    return this.keys[index];
  }

  indexOf(key: string): number {
    return this.positions.get(key) ?? -1;
  }

  /** Returns true when the height differs from what was recorded. */
  setHeight(key: string, height: number): boolean {
    const previous = this.heights.get(key);
    if (previous !== undefined && Math.abs(previous - height) < 0.5) return false;
    this.measuredSum += height - (previous ?? 0);
    this.heights.set(key, height);
    this.pinned.delete(key);
    this.offsets = null;
    return true;
  }

  offsetOf(index: number): number {
    const offsets = this.build();
    return offsets[Math.max(0, Math.min(index, this.keys.length))]!;
  }

  total(): number {
    return this.offsetOf(this.keys.length);
  }

  /** The row whose box contains `offset`, clamped to the first and last rows. */
  indexAt(offset: number): number {
    const n = this.keys.length;
    if (n === 0 || offset <= 0) return 0;
    const offsets = this.build();
    let lo = 0;
    let hi = n - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (offsets[mid]! <= offset) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  }

  private estimate(): number {
    return this.heights.size ? this.measuredSum / this.heights.size : this.initialEstimate;
  }

  private pin(key: string, estimate: number): number {
    const held = this.pinned.get(key);
    if (held !== undefined) return held;
    this.pinned.set(key, estimate);
    return estimate;
  }

  private build(): Float64Array {
    if (this.offsets) return this.offsets;
    const estimate = this.estimate();
    let lastMeasured = this.keys.length - 1;
    while (lastMeasured >= 0 && !this.heights.has(this.keys[lastMeasured]!)) lastMeasured--;
    const offsets = new Float64Array(this.keys.length + 1);
    for (let i = 0; i < this.keys.length; i++) {
      const key = this.keys[i]!;
      offsets[i + 1] =
        offsets[i]! +
        (this.heights.get(key) ?? (i < lastMeasured ? this.pin(key, estimate) : estimate));
    }
    this.offsets = offsets;
    return offsets;
  }
}
