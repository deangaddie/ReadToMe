// The ported Vitest specs use globals; bun test injects the same names at runtime.
declare const describe: typeof import('bun:test').describe;
declare const it: typeof import('bun:test').it;
declare const expect: typeof import('bun:test').expect;
declare const beforeEach: typeof import('bun:test').beforeEach;
declare const afterEach: typeof import('bun:test').afterEach;
