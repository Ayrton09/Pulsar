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

/** Byte counts as gibibytes with one decimal ("31.9 GB"), the scale of node memory and disks. */
export function useFormatGigabytes(): (bytes: number) => string {
  const { i18n } = useTranslation();
  return useMemo(() => {
    const format = new Intl.NumberFormat(i18n.language, {
      style: 'unit',
      unit: 'gigabyte',
      maximumFractionDigits: 1,
    });
    return (bytes: number) => format.format(bytes / 1024 ** 3);
  }, [i18n.language]);
}

/** Seconds in a .NET TimeSpan as JSON: "hh:mm:ss[.fffffff]" or "d.hh:mm:ss[.fffffff]". */
export function parseTimeSpan(value: string): number {
  const match = /^(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(value);
  if (!match) {
    return 0;
  }
  const [, days = '0', hours = '0', minutes = '0', seconds = '0'] = match;
  return Number(days) * 86_400 + Number(hours) * 3_600 + Number(minutes) * 60 + Number(seconds);
}

/** A coarse duration for uptimes: "3 d 4 h", "5 h 12 min", "7 min". */
export function useFormatUptime(): (timeSpan: string) => string {
  const { t } = useTranslation();
  return (timeSpan: string) => {
    const total = parseTimeSpan(timeSpan);
    const days = Math.floor(total / 86_400);
    const hours = Math.floor((total % 86_400) / 3_600);
    const minutes = Math.floor((total % 3_600) / 60);
    if (days > 0) {
      return t('format.days', { days, hours });
    }
    return hours > 0 ? t('format.hours', { hours, minutes }) : t('format.minutes', { minutes });
  };
}
