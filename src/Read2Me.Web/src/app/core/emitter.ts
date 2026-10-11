/** The few-line stand-in for an RxJS Subject: synchronous fan-out, unsubscribe by return value. */
export class Emitter<T> {
  #listeners = new Set<(value: T) => void>();

  emit(value: T): void {
    // A snapshot, so a listener that subscribes during the fan-out waits for the next value.
    for (const listener of Array.from(this.#listeners)) listener(value);
  }

  subscribe(listener: (value: T) => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }
}
