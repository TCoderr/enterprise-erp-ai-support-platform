import { useRef, useState } from 'react';
import { IntentKeys } from '../api/intent';
import type { ApiProblem } from '../api/problem';
import { asProblem, isAbort } from '../api/problem';

export interface Command<TArgs, TResult> {
  /** Resolves with the server result, or undefined when the command failed or was superseded. */
  run: (args: TArgs) => Promise<TResult | undefined>;
  pending: boolean;
  problem: ApiProblem | null;
  clearProblem: () => void;
}

interface CommandOptions<TArgs, TResult> {
  onSuccess?: (result: TResult, args: TArgs) => void | Promise<void>;
  onProblem?: (problem: ApiProblem, args: TArgs) => void;
}

/**
 * Runs one server command at a time. Results are reported only after the server answered; there is
 * no optimistic state. The Idempotency-Key handed to `execute` belongs to the user's intent: it is
 * reused when the identical arguments are retried after a network failure or 503.
 */
export function useCommand<TArgs, TResult>(
  execute: (args: TArgs, idempotencyKey: string) => Promise<TResult>,
  options: CommandOptions<TArgs, TResult> = {},
): Command<TArgs, TResult> {
  const [keys] = useState(() => new IntentKeys());
  const busy = useRef(false);
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<ApiProblem | null>(null);

  const run = async (args: TArgs): Promise<TResult | undefined> => {
    if (busy.current) {
      return undefined;
    }

    busy.current = true;
    setPending(true);
    setProblem(null);

    let result: TResult;
    try {
      result = await execute(args, keys.keyFor(args));
    } catch (error) {
      busy.current = false;
      setPending(false);
      if (isAbort(error)) {
        return undefined;
      }

      const failure = asProblem(error);
      if (!failure.isRetryable) {
        keys.settle();
      }

      setProblem(failure);
      options.onProblem?.(failure, args);
      return undefined;
    }

    keys.settle();
    busy.current = false;
    setPending(false);
    await options.onSuccess?.(result, args);
    return result;
  };

  return { run, pending, problem, clearProblem: () => setProblem(null) };
}
