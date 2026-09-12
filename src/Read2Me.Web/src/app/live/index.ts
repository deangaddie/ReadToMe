/**
 * Live hub client (ticket 07). Feature code imports from `@app/live`: the wire shapes, the
 * `LiveService` singleton and its state reducers. `hub-events.ts` holds the presentational event
 * shapes the stream components render (ticket 14 maps wire `llm`/`audioGen` messages onto them).
 */
export * from './live-messages';
export * from './live-connection';
export * from './live-state';
export * from './live.service';
