/**
 * What `record` holds itself under `key`, never what every object inherits ("constructor", "toString"): the keys are a
 * template's variables and ports, which may have those names.
 */
export function own<T>(record: Readonly<Record<string, T>> | null | undefined, key: string): T | undefined {
  return record != null && Object.hasOwn(record, key) ? record[key] : undefined;
}
