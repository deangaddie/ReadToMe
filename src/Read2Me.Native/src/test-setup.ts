import { GlobalRegistrator } from '@happy-dom/global-registrator';

// Keep Bun's fetch and the types it works with: happy-dom's fetch applies browser CORS rules, which
// the host-backed specs can't meet, and Bun's fetch does not take happy-dom's AbortSignal.
const native = {
  fetch: globalThis.fetch,
  Request: globalThis.Request,
  Response: globalThis.Response,
  Headers: globalThis.Headers,
  AbortController: globalThis.AbortController,
  AbortSignal: globalThis.AbortSignal,
};
GlobalRegistrator.register();
Object.assign(globalThis, native);
