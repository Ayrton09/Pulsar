import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import enCommon from '../locales/en/common.json';
import esCommon from '../locales/es/common.json';
import { visibleName } from '../lib/visibleText';

export const supportedLanguages = ['en', 'es'] as const;
export type SupportedLanguage = (typeof supportedLanguages)[number];

export const resources = {
  en: { common: enCommon },
  es: { common: esCommon },
} as const;

/** Picks the first supported language matching a BCP 47 tag such as "es-AR"; falls back to English. */
export function resolveLanguage(tag: string | undefined): SupportedLanguage {
  const base = tag?.toLowerCase().split('-')[0];
  return supportedLanguages.find((language) => language === base) ?? 'en';
}

/** Screen readers read the page in its `lang` (index.html says English until the interface has a language). */
function setDocumentLanguage(language: string) {
  document.documentElement.lang = language;
}

export async function initI18n(languageTag: string | undefined): Promise<void> {
  i18next.off('languageChanged', setDocumentLanguage);
  i18next.on('languageChanged', setDocumentLanguage);
  await i18next.use(initReactI18next).init({
    resources,
    lng: resolveLanguage(languageTag),
    fallbackLng: 'en',
    defaultNS: 'common',
    // React already escapes rendered values; what is left to do to a name put into a message (a confirmation too, which
    // React does not render) is to keep its hidden characters from reordering or hiding what the message says.
    interpolation: { escapeValue: true, escape: visibleName },
  });
  setDocumentLanguage(i18next.language);
}
