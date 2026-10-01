import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

/** Formats ISO timestamps from the API in the UI language and the browser's time zone. */
export function useFormatDate(): (iso: string) => string {
  const { i18n } = useTranslation();
  return useMemo(() => {
    const format = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' });
    return (iso: string) => format.format(new Date(iso));
  }, [i18n.language]);
}
