/**
 * The schedules the editor offers without writing cron (5 fields: minute hour day-of-month month day-of-week), and
 * back: a task whose cron matches one of them is edited as that kind.
 */
export type ScheduleKind =
  | { kind: 'daily'; hour: number; minute: number }
  | { kind: 'weekly'; day: number; hour: number; minute: number }
  | { kind: 'hours'; every: number }
  | { kind: 'custom'; cron: string };

export const HOUR_INTERVALS = [1, 2, 3, 4, 6, 8, 12] as const;

export function toCron(schedule: ScheduleKind): string {
  switch (schedule.kind) {
    case 'daily':
      return `${schedule.minute} ${schedule.hour} * * *`;
    case 'weekly':
      return `${schedule.minute} ${schedule.hour} * * ${schedule.day}`;
    case 'hours':
      return schedule.every === 1 ? '0 * * * *' : `0 */${schedule.every} * * *`;
    case 'custom':
      return schedule.cron.trim();
  }
}

// Minute and hour as cron writes them for a time of day ("0 4", "30 17").
const daily = /^([0-5]?\d) ([01]?\d|2[0-3]) \* \* \*$/;
const weekly = /^([0-5]?\d) ([01]?\d|2[0-3]) \* \* ([0-6])$/;

export function fromCron(cron: string): ScheduleKind {
  const value = cron.trim().replace(/\s+/g, ' ');
  let match = daily.exec(value);
  if (match) {
    return { kind: 'daily', minute: Number(match[1]), hour: Number(match[2]) };
  }
  match = weekly.exec(value);
  if (match) {
    return { kind: 'weekly', minute: Number(match[1]), hour: Number(match[2]), day: Number(match[3]) };
  }
  match = /^0 \*(?:\/(\d+))? \* \* \*$/.exec(value);
  const every = match ? Number(match[1] ?? 1) : NaN;
  if ((HOUR_INTERVALS as readonly number[]).includes(every)) {
    return { kind: 'hours', every };
  }
  return { kind: 'custom', cron: value };
}

/** "04:05" from hour and minute. */
export function timeOfDay(hour: number, minute: number): string {
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
}

/** The browser's IANA time zone (the editor's default). */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Every IANA zone the browser knows, plus UTC (browsers leave it out), with the current one first when the browser
 * does not know it (a task saved from another browser).
 */
export function timeZones(current: string): string[] {
  const known = ['UTC', ...(typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [])];
  return known.includes(current) ? known : [current, ...known];
}
