// Unit checks for the environment resolution in browse.mjs: `node --test` or `bun test` here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resolveApp } from './browse.mjs';

test('defaults to the Angular app on its dev server', () => {
  assert.deepEqual(resolveApp({}), { app: '/app', web: 'http://localhost:4200' });
});

test('R2M_APP=/app2 switches the default dev server to :4300', () => {
  assert.deepEqual(resolveApp({ R2M_APP: '/app2' }), { app: '/app2', web: 'http://localhost:4300' });
});

test('R2M_APP is normalised to one leading slash and no trailing slash', () => {
  assert.equal(resolveApp({ R2M_APP: 'app2/' }).app, '/app2');
  assert.equal(resolveApp({ R2M_APP: '/app/' }).app, '/app');
});

test('R2M_WEB wins over the per-app default', () => {
  assert.equal(resolveApp({ R2M_APP: '/app2', R2M_WEB: 'http://localhost:5000' }).web, 'http://localhost:5000');
});
