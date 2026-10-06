/**
 * The path the app is served under. `build.ts` derives the output folder, the asset `publicPath`
 * and the built `<base href>` from it, and `dev.ts` serves under it. The dev server serves the
 * source `index.html` as written, so `base.spec.ts` holds that file's `<base href>` to this value.
 */
export const BASE = '/app2/';
