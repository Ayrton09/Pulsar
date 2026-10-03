import { createHmac } from 'node:crypto';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

/** The 30-second step a time falls in (RFC 6238). */
export function timeStep(now = Date.now()): number {
  return Math.floor(now / 1000 / 30);
}

/** The six-digit code an authenticator app shows for a Base32 key (spaces ignored) at a time step. */
export function totp(key: string, step = timeStep()): string {
  let bits = '';
  for (const char of key.replace(/[\s=]/g, '').toUpperCase()) {
    bits += ALPHABET.indexOf(char).toString(2).padStart(5, '0');
  }
  const secret = Buffer.alloc(Math.floor(bits.length / 8));
  for (let i = 0; i < secret.length; i++) {
    secret[i] = parseInt(bits.slice(i * 8, i * 8 + 8), 2);
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hash = createHmac('sha1', secret).update(counter).digest();
  const offset = hash.readUInt8(hash.length - 1) & 0x0f;
  const value = (hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return value.toString().padStart(6, '0');
}

/**
 * A code for a step after `afterStep`: the panel accepts each code once, so a second use of the same key waits for the
 * next 30-second step.
 */
export async function freshTotp(key: string, afterStep: number): Promise<{ code: string; step: number }> {
  while (timeStep() <= afterStep) {
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  const step = timeStep();
  return { code: totp(key, step), step };
}
