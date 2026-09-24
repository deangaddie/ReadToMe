import { ApiError, DiscoveryOutcomeDto } from '@app/api';
import {
  applyRows,
  discoveryErrorMessage,
  discoveryFailureMessage,
  rowCollisions,
  toDiscoveryRows,
  withAlias,
  withoutAlias,
} from './discovery-rows';

const outcome: DiscoveryOutcomeDto = {
  status: 'Ok',
  reason: null,
  characters: [
    { name: 'Elizabeth Bennet', aliases: ['Lizzy'], existingCharacterId: 'eliza' },
    { name: 'Jane Bennet', aliases: ['Miss Bennet'], existingCharacterId: null },
  ],
  collisions: [],
};

describe('discovery rows', () => {
  it('starts every row included and remembers what it resolves onto', () => {
    expect(toDiscoveryRows(outcome)).toEqual([
      { name: 'Elizabeth Bennet', aliases: ['Lizzy'], included: true, existingCharacterId: 'eliza' },
      { name: 'Jane Bennet', aliases: ['Miss Bennet'], included: true, existingCharacterId: null },
    ]);
  });

  it('recomputes collisions over included rows only, folding the row that already exists', () => {
    const rows = toDiscoveryRows(outcome);
    // Elizabeth's row folds into her roster entry (no self-collision on Lizzy); Jane's alias
    // collides with Mary's.
    const roster = [
      { id: 'eliza', name: 'Elizabeth Bennet', aliases: ['Lizzy'] },
      { id: 'mary', name: 'Mary Bennet', aliases: ['Miss Bennet'] },
    ];
    expect(rowCollisions(rows, roster)).toEqual(['Miss Bennet']);
    rows[1]!.included = false;
    expect(rowCollisions(rows, roster)).toEqual([]);
  });

  it('applies included rows with a name, trimmed', () => {
    const rows = toDiscoveryRows(outcome);
    rows[0]!.included = false;
    rows[1]!.name = '  Jane Bennet ';
    rows[1]!.aliases = [' Miss Bennet ', ''];
    rows.push({ name: '   ', aliases: [], included: true, existingCharacterId: null });
    expect(applyRows(rows)).toEqual([{ name: 'Jane Bennet', aliases: ['Miss Bennet'] }]);
  });

  it('adds an alias once and removes by value', () => {
    const row = toDiscoveryRows(outcome)[0]!;
    expect(withAlias(row, ' Eliza ').aliases).toEqual(['Lizzy', 'Eliza']);
    expect(withAlias(row, 'lizzy')).toBe(row);
    expect(withAlias(row, '  ')).toBe(row);
    expect(withoutAlias(row, 'Lizzy').aliases).toEqual([]);
  });

  it('maps the failure states to messages', () => {
    expect(discoveryFailureMessage(outcome)).toBeNull();
    expect(
      discoveryFailureMessage({ ...outcome, status: 'ServiceUnavailable', reason: 'down' }),
    ).toBe('The LLM service is unavailable: down');
    expect(discoveryFailureMessage({ ...outcome, status: 'Failed', reason: 'bad json' })).toBe(
      'Character discovery failed: bad json',
    );
    expect(discoveryErrorMessage(new ApiError(422, 'Unprocessable', 'No active LLM'))).toBe(
      'No LLM server is configured. Set one up under Settings → LLM.',
    );
    expect(discoveryErrorMessage(new ApiError(0, 'Host unreachable'))).toBe(
      'Character discovery failed: Host unreachable',
    );
  });
});
