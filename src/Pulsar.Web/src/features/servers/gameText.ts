import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

interface GameTexts {
  variables?: Record<string, { label?: string; description?: string; options?: Record<string, string> }>;
  ports?: Record<string, { label?: string; description?: string }>;
}

interface VariableText {
  key: string;
  label: string;
  description: string | null;
  options: { value: string; label: string }[];
}

interface PortText {
  name: string;
  label: string;
  description?: string | null;
}

/**
 * The bundled games' labels in the reader's language (the locales' gameText, by template key), else the template's own
 * English: an imported game, or a text not translated yet. Looked up as objects: an option's value may hold a dot.
 */
export function useGameText(templateKey: string | undefined) {
  const { i18n } = useTranslation();
  const texts = (templateKey ? i18n.getResource(i18n.language, 'common', `gameText.${templateKey}`) : undefined) as
    GameTexts | undefined;
  // The same functions while the language and its texts stay: screens memoize what they translate with them.
  return useMemo(
    () => ({
      /** The variables with their labels, descriptions and choices in the reader's language. */
      variables: <T extends VariableText>(variables: T[]): T[] =>
        variables.map((variable) => {
          const text = texts?.variables?.[variable.key];
          return {
            ...variable,
            label: text?.label ?? variable.label,
            description: text?.description ?? variable.description,
            options: variable.options.map((option) => ({
              ...option,
              label: text?.options?.[option.value] ?? option.label,
            })),
          };
        }),
      /** The ports with their labels and descriptions in the reader's language. */
      ports: <T extends PortText>(ports: T[]): T[] =>
        ports.map((port) => {
          const text = texts?.ports?.[port.name];
          return {
            ...port,
            label: text?.label ?? port.label,
            description: text?.description ?? port.description ?? null,
          };
        }),
    }),
    [texts],
  );
}
