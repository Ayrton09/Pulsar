import type { TFunction } from 'i18next';
import type { Schemas } from '../../api/client';

/**
 * A role's description in the reader's language. The built-in role's is the panel's own text, not the English one stored
 * with it when the database was made; the others' are what their administrators wrote.
 */
export function roleDescription(
  t: TFunction,
  role: Pick<Schemas['RoleSummary'], 'isSystem' | 'description'>,
): string | null {
  return role.isSystem ? t('roles.builtInDescription') : role.description;
}
