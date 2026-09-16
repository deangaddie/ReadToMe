import { AliasClaim, AliasOwner, collides, findAliasCollisions } from './alias-collisions';

/** Mirrors `Read2Me.Tests/Core/AliasCollisionsTests.cs`; keep the two in step. */
describe('findAliasCollisions', () => {
  let nextId = 0;
  const row = (name: string, ...aliases: string[]): AliasClaim => ({ name, aliases });
  const existing = (name: string, ...aliases: string[]): AliasOwner => ({
    id: `c${++nextId}`,
    name,
    aliases,
  });

  it('no shared names: no collisions', () => {
    expect(
      findAliasCollisions([row('Elizabeth Bennet', 'Lizzy'), row('Jane Bennet', 'Jane')], []),
    ).toEqual([]);
  });

  it('the same alias on two rows is a collision', () => {
    // The observed Pride and Prejudice case: discovery hands "Miss Bennet" to every sister.
    expect(
      findAliasCollisions(
        [
          row('Elizabeth Bennet', 'Lizzy', 'Miss Bennet'),
          row('Jane Bennet', 'Miss Bennet'),
          row('Mary Bennet', 'Miss Bennet'),
        ],
        [],
      ),
    ).toEqual(['Miss Bennet']);
  });

  it("a row's name matching another row's alias is a collision", () => {
    expect(
      findAliasCollisions([row('Miss Bennet'), row('Elizabeth Bennet', 'Miss Bennet')], []),
    ).toEqual(['Miss Bennet']);
  });

  it('a collision with the existing roster is found', () => {
    expect(
      findAliasCollisions(
        [row('Jane Bennet', 'Miss Bennet')],
        [existing('Elizabeth Bennet', 'Miss Bennet')],
      ),
    ).toEqual(['Miss Bennet']);
  });

  it('the roster character a row resolves onto is not a second owner', () => {
    // Re-running discovery re-proposes characters that already exist. A row merging into
    // Elizabeth is Elizabeth — counting the roster row too would flag every alias it keeps.
    const elizabeth = existing('Elizabeth Bennet', 'Lizzy');
    const claim: AliasClaim = {
      ...row('Elizabeth Bennet', 'Lizzy'),
      existingCharacterId: elizabeth.id,
    };
    expect(findAliasCollisions([claim], [elizabeth])).toEqual([]);
  });

  it('matching is case and whitespace insensitive', () => {
    const found = findAliasCollisions(
      [row('Elizabeth Bennet', ' miss bennet '), row('Jane Bennet', 'Miss Bennet')],
      [],
    );
    expect(found.map((f) => f.toLowerCase())).toEqual(['miss bennet']);
    expect(collides(found, 'MISS BENNET ')).toBe(true);
    expect(collides(found, 'Lizzy')).toBe(false);
  });

  it('one row repeating its own alias is not a collision', () => {
    // Untidy, not ambiguous: it still resolves to one character.
    expect(findAliasCollisions([row('Elizabeth Bennet', 'Lizzy', 'lizzy')], [])).toEqual([]);
  });

  it('blank names are ignored', () => {
    expect(
      findAliasCollisions([row('Elizabeth Bennet', '  '), row('Jane Bennet', '')], []),
    ).toEqual([]);
  });
});
