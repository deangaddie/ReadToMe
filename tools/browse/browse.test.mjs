// Unit checks for the environment resolution in browse.mjs: `bun test` here.
import { expect, test } from 'bun:test';
import { resolveWeb } from './browse.mjs';

test('defaults to the dev server', () => {
  expect(resolveWeb({})).toBe('http://localhost:4300');
});

test('R2M_WEB wins, without a trailing slash', () => {
  expect(resolveWeb({ R2M_WEB: 'http://localhost:5000/' })).toBe('http://localhost:5000');
});
