import { AppTheme } from '@app/api';
import { DEFAULT_DRAFT, fromDraft, isDraftValid, toDraft, validateDraft } from './theme-form';

describe('theme form', () => {
  it('requires a name and valid primary/secondary', () => {
    expect(validateDraft({ ...DEFAULT_DRAFT, name: '  ' })).toEqual({ name: 'Name is required' });
    expect(validateDraft({ ...DEFAULT_DRAFT, name: 'x', primary: 'orange' })).toEqual({
      primary: 'Use #rgb or #rrggbb',
    });
    expect(isDraftValid({ ...DEFAULT_DRAFT, name: 'x' })).toBe(true);
  });

  it('accepts blank optional colours but rejects malformed ones', () => {
    expect(isDraftValid({ ...DEFAULT_DRAFT, name: 'x', surface: '' })).toBe(true);
    expect(isDraftValid({ ...DEFAULT_DRAFT, name: 'x', surface: '#abc' })).toBe(true);
    expect(validateDraft({ ...DEFAULT_DRAFT, name: 'x', surface: '#abcd' })).toEqual({
      surface: 'Use #rgb or #rrggbb, or clear',
    });
  });

  it('round-trips a theme through the draft, normalising hex and blanking to null', () => {
    const theme: AppTheme = {
      id: 4,
      name: 'Sunset',
      isBuiltIn: false,
      isDark: false,
      primary: '#FF6F61',
      secondary: '#FB3',
      background: '#FFF5F2',
      surface: null,
    };
    const draft = toDraft(theme);
    expect(draft.primary).toBe('#ff6f61');
    expect(draft.secondary).toBe('#ffbb33');
    expect(draft.surface).toBe('');

    const body = fromDraft({ ...draft, name: ' Sunset 2 ', textPrimary: '  ' });
    expect(body.name).toBe('Sunset 2');
    expect(body.background).toBe('#fff5f2');
    expect(body.surface).toBeNull();
    expect(body.textPrimary).toBeNull();
  });
});
