/** Receipts arrive in bursts (an attribution run commits per paragraph); refetch once per burst. */
export const REFETCH_DEBOUNCE_MS = 250;

/** Runs a refetch once, {@link REFETCH_DEBOUNCE_MS} after the first of a burst of requests for it. */
export class Debounced {
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly run: () => Promise<void>) {}

  schedule(): void {
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.run().catch(() => undefined);
    }, REFETCH_DEBOUNCE_MS);
  }

  cancel(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
