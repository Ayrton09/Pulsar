import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import enCommon from '../locales/en/common.json';
import esCommon from '../locales/es/common.json';

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

export async function initI18n(languageTag: string | undefined): Promise<void> {
  await i18next.use(initReactI18next).init({
    resources,
    lng: resolveLanguage(languageTag),
    fallbackLng: 'en',
    defaultNS: 'common',
    interpolation: { escapeValue: false }, // React already escapes rendered values.
  });
}
