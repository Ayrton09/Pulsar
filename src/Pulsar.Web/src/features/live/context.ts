import { createContext } from 'react';
import type { LiveChannel } from './channel';

/** The tab's live channel; null before it exists and outside the signed-in area. */
export const LiveContext = createContext<LiveChannel | null>(null);

/** Usage samples pushed for a server (kept outside ['servers'] so refreshing a server never refetches them). */
export const metricsKey = (serverId: string) => ['live', 'serverMetrics', serverId] as const;
