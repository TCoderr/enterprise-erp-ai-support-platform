export interface FieldProblem {
  field: string;
  code: string;
  detail: string;
}

export interface ProblemInit {
  status: number;
  code: string;
  title: string;
  detail?: string | undefined;
  userCode?: string | undefined;
  correlationId?: string | undefined;
  errors?: FieldProblem[] | undefined;
  retryAfterSeconds?: number | undefined;
}

/**
 * A failed API call. `status` is the HTTP status, or 0 when no response arrived at all
 * (network down, connection reset): in that case the outcome of a command is unknown.
 */
export class ApiProblem extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly detail: string | undefined;
  readonly userCode: string | undefined;
  readonly correlationId: string | undefined;
  readonly errors: readonly FieldProblem[];
  readonly retryAfterSeconds: number | undefined;

  constructor(init: ProblemInit) {
    super(init.title);
    this.name = 'ApiProblem';
    this.status = init.status;
    this.code = init.code;
    this.title = init.title;
    this.detail = init.detail;
    this.userCode = init.userCode;
    this.correlationId = init.correlationId;
    this.errors = init.errors ?? [];
    this.retryAfterSeconds = init.retryAfterSeconds;
  }

  /** No response was received; the request may or may not have reached the server. */
  get isNetwork(): boolean {
    return this.status === 0;
  }

  /** The server answered but could not complete the request now; nothing was applied. */
  get isUnavailable(): boolean {
    return this.status === 503 || this.status === 502 || this.status === 504;
  }

  /** Safe to send the very same command again with the same Idempotency-Key. */
  get isRetryable(): boolean {
    return this.isNetwork || this.isUnavailable;
  }

  get isStale(): boolean {
    return this.status === 412;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isForbidden(): boolean {
    return this.status === 403;
  }

  get isRateLimited(): boolean {
    return this.status === 429;
  }
}

/** Thrown for a response that belongs to a session that has since ended; never rendered. */
export class SupersededError extends Error {
  constructor() {
    super('Yanıt önceki oturuma ait');
    this.name = 'SupersededError';
  }
}

export function isAbort(error: unknown): boolean {
  return (
    error instanceof SupersededError ||
    (error instanceof DOMException && error.name === 'AbortError') ||
    (error instanceof Error && error.name === 'AbortError')
  );
}

export function asProblem(error: unknown): ApiProblem {
  if (error instanceof ApiProblem) {
    return error;
  }

  return new ApiProblem({
    status: -1,
    code: 'client_error',
    title: 'Beklenmeyen bir istemci hatası oluştu',
    detail: 'Sayfayı yenileyip yeniden deneyin.',
  });
}

const FALLBACK_TITLES: Record<number, string> = {
  400: 'İstek doğrulanamadı',
  401: 'Oturum açmanız gerekiyor',
  403: 'Bu işlem için yetkiniz yok',
  404: 'Kayıt bulunamadı',
  409: 'İstek mevcut durumla çakışıyor',
  412: 'Kayıt siz görüntüledikten sonra değişti',
  413: 'İstek gövdesi çok büyük',
  422: 'Satır belgeyle eşleşmiyor',
  428: 'If-Match başlığı gerekli',
  429: 'Çok fazla istek gönderildi',
  500: 'Beklenmeyen bir hata oluştu',
  503: 'İşlem şu anda tamamlanamadı; daha sonra yeniden deneyin',
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

/** Builds a typed problem from a problem+json body; tolerates a missing or foreign body. */
export function problemFromBody(status: number, body: unknown, retryAfter: string | null): ApiProblem {
  const record = isRecord(body) ? body : {};
  const errors: FieldProblem[] = [];
  if (Array.isArray(record.errors)) {
    for (const item of record.errors) {
      if (isRecord(item)) {
        errors.push({
          field: str(item.field) ?? '',
          code: str(item.code) ?? '',
          detail: str(item.detail) ?? '',
        });
      }
    }
  }

  let retryAfterSeconds: number | undefined;
  if (retryAfter !== null && /^\d+$/.test(retryAfter.trim())) {
    retryAfterSeconds = Number(retryAfter.trim());
  }

  return new ApiProblem({
    status,
    code: str(record.code) ?? `http_${status}`,
    title: str(record.title) ?? FALLBACK_TITLES[status] ?? 'İstek tamamlanamadı',
    detail: str(record.detail),
    userCode: str(record.userCode),
    correlationId: str(record.correlationId),
    errors,
    retryAfterSeconds,
  });
}

export function networkProblem(): ApiProblem {
  return new ApiProblem({
    status: 0,
    code: 'network_error',
    title: 'Sunucuya ulaşılamadı',
    detail: 'Bağlantınızı kontrol edin. İşlemin sonucu bilinmiyor; aynı işlemi güvenle yeniden deneyebilirsiniz.',
  });
}
