/** A fresh Idempotency-Key: 36 characters of [0-9a-f-], inside the 16–128 [A-Za-z0-9_-] the API accepts. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/**
 * One Idempotency-Key per user intent. The key is kept while the same payload is sent again after an
 * unknown outcome (network failure, 503) and replaced as soon as the payload changes or the command
 * reaches a definite outcome.
 */
export class IntentKeys {
  private key: string | null = null;
  private fingerprint: string | null = null;

  keyFor(payload: unknown): string {
    const fingerprint = JSON.stringify(payload) ?? '';
    if (this.key === null || this.fingerprint !== fingerprint) {
      this.key = newIdempotencyKey();
      this.fingerprint = fingerprint;
    }

    return this.key;
  }

  /** The server accepted or definitively rejected the command: the next attempt is a new intent. */
  settle(): void {
    this.key = null;
    this.fingerprint = null;
  }
}
