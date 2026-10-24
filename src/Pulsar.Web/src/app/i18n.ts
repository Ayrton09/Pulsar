import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import enCommon from '../locales/en/common.json';
import { visibleName } from '../lib/visibleText';

// Portuguese is Brazil's, Chinese is Simplified (mainland China's).
export const supportedLanguages = ['en', 'es', 'pt', 'fr', 'it', 'de', 'ja', 'ko', 'zh'] as const;
export type SupportedLanguage = (typeof supportedLanguages)[number];

/** Each language by its own name, as lists of languages show them (the same whatever the interface's language). */
export const languageNames: Record<SupportedLanguage, string> = {
  en: 'English',
  es: 'Español',
  pt: 'Português (Brasil)',
  fr: 'Français',
  it: 'Italiano',
  de: 'Deutsch',
  ja: '日本語',
  ko: '한국어',
  zh: '简体中文',
};

type Translation = typeof enCommon;

/**
 * English is in the bundle (every other language falls back to it); the others are fetched when chosen, so a browser
 * downloads one language, not nine.
 */
const translations: Record<Exclude<SupportedLanguage, 'en'>, () => Promise<{ default: Translation }>> = {
  es: () => import('../locales/es/common.json'),
  pt: () => import('../locales/pt/common.json'),
  fr: () => import('../locales/fr/common.json'),
  it: () => import('../locales/it/common.json'),
  de: () => import('../locales/de/common.json'),
  ja: () => import('../locales/ja/common.json'),
  ko: () => import('../locales/ko/common.json'),
  zh: () => import('../locales/zh/common.json'),
};

/** Where this browser keeps the language chosen in My account. */
const storageKey = 'pulsar.language';

/** Picks the first supported language matching a BCP 47 tag such as "es-AR"; falls back to English. */
export function resolveLanguage(tag: string | undefined): SupportedLanguage {
  const base = tag?.toLowerCase().split('-')[0];
  return supportedLanguages.find((language) => language === base) ?? 'en';
}

/** The language chosen in this browser (My account), when there is one it still has. */
export function storedLanguage(): SupportedLanguage | undefined {
  try {
    const stored = localStorage.getItem(storageKey);
    return supportedLanguages.find((language) => language === stored);
  } catch {
    return undefined; // storage refused (a private window, site data blocked): the browser's language
  }
}

/** The texts of <paramref name="language"/>, fetched once. Fails when they cannot be (offline, or a newer panel). */
async function texts(language: SupportedLanguage): Promise<Translation> {
  if (language === 'en') {
    return enCommon;
  }
  return (await translations[language]()).default;
}

/** Fetches the texts of <paramref name="language"/> into i18next, once, without changing the interface's language. */
export async function loadLanguage(language: SupportedLanguage): Promise<void> {
  if (!i18next.hasResourceBundle(language, 'common')) {
    i18next.addResourceBundle(language, 'common', await texts(language));
  }
}

/**
 * Changes the interface's language and keeps the choice in this browser, for the next visits. Fails, changing
 * nothing, when the language's texts cannot be fetched.
 */
export async function chooseLanguage(language: SupportedLanguage): Promise<void> {
  await loadLanguage(language);
  try {
    localStorage.setItem(storageKey, language);
  } catch {
    // Not kept: this visit only.
  }
  await i18next.changeLanguage(language);
}

/**
 * Screen readers read the page in its `lang`, and browsers pick CJK glyphs by it (index.html says English until the
 * interface has a language): Chinese is zh-CN, Portuguese pt-BR.
 */
function setDocumentLanguage(language: string) {
  document.documentElement.lang = language === 'zh' ? 'zh-CN' : language === 'pt' ? 'pt-BR' : language;
}

/** The language chosen in this browser, or the one of <paramref name="languageTag"/> (the browser's). */
export async function initI18n(languageTag: string | undefined): Promise<void> {
  i18next.off('languageChanged', setDocumentLanguage);
  i18next.on('languageChanged', setDocumentLanguage);
  let language = storedLanguage() ?? resolveLanguage(languageTag);
  const resources: Partial<Record<SupportedLanguage, { common: Translation }>> = { en: { common: enCommon } };
  try {
    resources[language] = { common: await texts(language) };
  } catch {
    language = 'en'; // its texts could not be fetched: English rather than no panel at all
  }
  await i18next.use(initReactI18next).init({
    resources,
    lng: language,
    fallbackLng: 'en',
    defaultNS: 'common',
    // React already escapes rendered values; what is left to do to a name put into a message (a confirmation too, which
    // React does not render) is to keep its hidden characters from reordering or hiding what the message says.
    interpolation: { escapeValue: true, escape: visibleName },
  });
  setDocumentLanguage(i18next.language);
}
