import { ApiError, ApplyDiscoveryRow, DiscoveryOutcomeDto, Guid } from '@app/api';
import { AliasOwner, findAliasCollisions } from './alias-collisions';

/**
 * One proposed character in the discovery review (research §4 "Discover dialog"). The dialog
 * edits these in place and performs no writes; only included rows are applied.
 */
export interface DiscoveryRow {
  name: string;
  aliases: string[];
  included: boolean;
  /** The roster character this row resolves onto when applied — the "Already exists" row. */
  existingCharacterId: Guid | null;
}

export function toDiscoveryRows(outcome: DiscoveryOutcomeDto): DiscoveryRow[] {
  return outcome.characters.map((c) => ({
    name: c.name,
    aliases: [...c.aliases],
    included: true,
    existingCharacterId: c.existingCharacterId ?? null,
  }));
}

/** The strings two characters would share once the included rows are applied. */
export function rowCollisions(rows: readonly DiscoveryRow[], roster: readonly AliasOwner[]): string[] {
  return findAliasCollisions(
    rows.filter((r) => r.included),
    roster,
  );
}

/** What "Add N selected" posts: the included rows with a name. */
export function applyRows(rows: readonly DiscoveryRow[]): ApplyDiscoveryRow[] {
  return rows
    .filter((r) => r.included && r.name.trim().length > 0)
    .map((r) => ({ name: r.name.trim(), aliases: r.aliases.map((a) => a.trim()).filter(Boolean) }));
}

/** Adds an alias unless blank or already present (case-insensitively). Returns the row unchanged otherwise. */
export function withAlias(row: DiscoveryRow, alias: string): DiscoveryRow {
  const name = alias.trim();
  if (!name || row.aliases.some((a) => a.toLowerCase() === name.toLowerCase())) return row;
  return { ...row, aliases: [...row.aliases, name] };
}

export function withoutAlias(row: DiscoveryRow, alias: string): DiscoveryRow {
  return { ...row, aliases: row.aliases.filter((a) => a !== alias) };
}

/**
 * The failure banner. A 422 is the host saying no LLM server is configured; the outcome statuses
 * are the discovery service's own; anything else is the transport.
 */
export function discoveryFailureMessage(outcome: DiscoveryOutcomeDto): string | null {
  switch (outcome.status) {
    case 'Ok':
      return null;
    case 'ServiceUnavailable':
      return `The LLM service is unavailable: ${outcome.reason ?? 'no reason given'}`;
    default:
      return `Character discovery failed: ${outcome.reason ?? 'no reason given'}`;
  }
}

export function discoveryErrorMessage(error: ApiError): string {
  if (error.status === 422)
    return 'No LLM server is configured. Set one up under Settings → LLM.';
  return `Character discovery failed: ${error.detail ?? error.message}`;
}
