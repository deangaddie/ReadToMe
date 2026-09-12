import { workspaceUrl } from './workspace-url';

describe('workspaceUrl', () => {
  it('encodes folder and file segments and keeps nested paths', () => {
    expect(workspaceUrl('My Book', 'audio/item 1.wav')).toBe(
      '/workspace/My%20Book/audio/item%201.wav',
    );
  });

  it('appends a version cache-buster only when one is given', () => {
    expect(workspaceUrl('f', 'a.wav', 3)).toBe('/workspace/f/a.wav?v=3');
    expect(workspaceUrl('f', 'a.wav', null)).toBe('/workspace/f/a.wav');
    expect(workspaceUrl('f', 'a.wav', '')).toBe('/workspace/f/a.wav');
  });
});
