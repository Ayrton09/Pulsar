import { z } from 'zod';

/** Name, description and public host of a node (limits as in Pulsar.Domain.Nodes.Node). */
export const nodeSchema = z.object({
  name: z.string().trim().min(1, 'validation.required').max(64, 'validation.tooLong'),
  description: z.string().trim().max(512, 'validation.tooLong'),
  publicHost: z.string().trim().max(255, 'validation.tooLong'),
});

export type NodeFormValues = z.infer<typeof nodeSchema>;
