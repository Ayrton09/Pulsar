import type { QueryClient } from '@tanstack/react-query';
import { createContext } from 'react';
import type { Schemas } from '../api/client';

/** Moves a newly observed identity into a fresh cache and component tree. */
export const SessionIdentityBoundary = createContext<
  ((source: QueryClient, next: Schemas['SessionResponse']) => void) | null
>(null);
