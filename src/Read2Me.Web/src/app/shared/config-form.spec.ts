import { duplicateName, isAbsoluteUrl } from './config-form';

describe('config form helpers', () => {
  it('names a duplicate so it does not collide', () => {
    expect(duplicateName('Local', ['Local'])).toBe('Local (copy)');
    expect(duplicateName('Local', ['Local', 'Local (copy)'])).toBe('Local (copy 2)');
    expect(duplicateName('Local', ['Local', 'local (COPY)', 'Local (copy 2)'])).toBe(
      'Local (copy 3)',
    );
  });

  it('takes a URL with a scheme and a host, and nothing less', () => {
    expect(isAbsoluteUrl(' http://localhost:8003 ')).toBe(true);
    expect(isAbsoluteUrl('localhost:8003')).toBe(false);
    expect(isAbsoluteUrl('/relative')).toBe(false);
    expect(isAbsoluteUrl('')).toBe(false);
  });
});
