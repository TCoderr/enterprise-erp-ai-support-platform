import type { UseQueryResult } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { ApiProblem } from '../api/problem';
import { asProblem } from '../api/problem';
import { useAssistOptional } from '../shell/context';
import { Icon } from './Icon';
import type { IconName } from './Icon';
import { Button, Code, DocumentTitle } from './ui';

// Every data view has an honest state for: loading, empty, failed, stale, not found / not allowed.

export function LoadingRegion({ label, rows = 4 }: { label: string; rows?: number }) {
  return (
    <div className="region" role="status" aria-busy="true" aria-label={label}>
      {Array.from({ length: rows }, (_, index) => (
        <div className="skeleton-row" key={index} aria-hidden="true">
          <span />
          <span />
          <span />
          <span />
        </div>
      ))}
      <p className="skeleton-note">{label}</p>
    </div>
  );
}

interface EmptyStateProps {
  title: string;
  children?: ReactNode;
  action?: ReactNode;
  icon?: IconName;
  /** Heading level inside the page hierarchy. */
  level?: 2 | 3;
}

export function EmptyState({ title, children, action, icon = 'tray', level = 2 }: EmptyStateProps) {
  const Heading = level === 2 ? 'h2' : 'h3';
  return (
    <div className="region empty">
      <Icon name={icon} size={32} />
      <Heading>{title}</Heading>
      {children !== undefined && <p>{children}</p>}
      {action}
    </div>
  );
}

/** A document outside the caller's scope is indistinguishable from a missing one, here as on the server. */
export function NotFoundState({ what = 'Kayıt', action }: { what?: string; action?: ReactNode }) {
  return (
    <EmptyState title={`${what} bulunamadı veya erişiminiz yok`} icon="lock" action={action}>
      Bağlantıyı ve numarayı kontrol edin. Kayıt başka bir depoya ya da başka bir kullanıcıya aitse görüntülemek için yetkiniz
      olması gerekir; yetki için sistem yöneticinize başvurun.
    </EmptyState>
  );
}

export function ForbiddenState({ problem, action }: { problem?: ApiProblem | undefined; action?: ReactNode }) {
  return (
    <EmptyState title="Bu işlem için yetkiniz yok" icon="lock" action={action}>
      Rolünüz bu ekranı veya işlemi kapsamıyor. Gerekliyse sistem yöneticinizden yetki isteyin.
      {problem?.correlationId !== undefined && (
        <>
          {' '}
          <span className="corr">
            Korelasyon kimliği: <Code>{problem.correlationId}</Code>
          </span>
        </>
      )}
    </EmptyState>
  );
}

interface ProblemAlertProps {
  problem: ApiProblem;
  /** Overrides the server's title with a sentence about the action that failed. */
  title?: string | undefined;
  onRetry?: (() => void) | undefined;
  retryLabel?: string;
  retryPending?: boolean;
  /** Field errors already shown next to their inputs are left out of the list. */
  hideFieldErrors?: boolean;
  children?: ReactNode;
  headingLevel?: 2 | 3;
}

function explain(problem: ApiProblem): string | undefined {
  if (problem.isNetwork) {
    return 'Sunucuya ulaşılamadı. İşlemin sonucu bilinmiyor; aynı işlemi güvenle yeniden deneyebilirsiniz — ikinci kez uygulanmaz.';
  }

  if (problem.isUnavailable) {
    return 'İşlem uygulanmadı. Birkaç saniye sonra yeniden deneyin; sorun sürerse destek talebi açarken aşağıdaki kimliği ekleyin.';
  }

  if (problem.isRateLimited) {
    return problem.retryAfterSeconds !== undefined
      ? `Kısa sürede çok fazla istek gönderildi. ${problem.retryAfterSeconds} saniye sonra yeniden deneyin.`
      : 'Kısa sürede çok fazla istek gönderildi. Biraz bekleyip yeniden deneyin.';
  }

  switch (problem.code) {
    case 'duplicate_business_reference':
      return 'Bu referansla daha önce bir kayıt yapılmış. Yeni bir kayıt oluşturulmadı ve stok ikinci kez değişmedi; referansı kontrol edin.';
    case 'approval_limit_exceeded':
      return 'Siparişin toplamı sizin onay limitinizin üzerinde. Limiti yeterli bir satın alma uzmanı onaylayabilir ya da sistem yöneticisi limitinizi güncelleyebilir.';
    case 'invalid_state':
      return 'Belgenin durumu bu işleme izin vermiyor; belge başka bir kullanıcı tarafından ilerletilmiş veya kapatılmış olabilir.';
    case 'master_data_inactive':
      return 'Belgedeki müşteri, tedarikçi, depo ya da ürünlerden biri pasif. Pasif kayıt yeni işlemde kullanılamaz.';
    case 'idempotency_key_reused':
      return 'Aynı işlem anahtarı farklı içerikle gönderildi. Sayfayı yenileyip işlemi baştan yapın.';
    case 'precondition_required':
      return 'Beklenmeyen bir istemci hatası: belge sürümü gönderilmedi. Sayfayı yenileyip yeniden deneyin.';
    case 'forbidden':
      return 'Rolünüz ya da depo yetkiniz bu işlemi kapsamıyor.';
    case 'not_found':
      return 'Kayıt bulunamadı veya erişiminiz yok.';
    default:
      return undefined;
  }
}

/** Problem details as the server reported them: what happened, what to do, and the id support will ask for. */
export function ProblemAlert({
  problem,
  title,
  onRetry,
  retryLabel = 'Yeniden dene',
  retryPending = false,
  hideFieldErrors = false,
  children,
  headingLevel = 2,
}: ProblemAlertProps) {
  const assist = useAssistOptional();
  const Heading = headingLevel === 2 ? 'h2' : 'h3';
  const tone = problem.isRateLimited ? 'warn' : 'danger';
  const guidance = explain(problem);
  const fieldErrors = hideFieldErrors ? [] : problem.errors.filter((e) => e.detail.length > 0);
  const { userCode } = problem;

  return (
    <div className={`alert alert--${tone}`} role="alert">
      <Icon name={tone === 'warn' ? 'warn' : 'error'} />
      <Heading>{title ?? problem.title}</Heading>
      {title !== undefined && title !== problem.title && <p>{problem.title}</p>}
      {problem.detail !== undefined && <p>{problem.detail}</p>}
      {fieldErrors.length > 0 && (
        <ul>
          {fieldErrors.map((error, index) => (
            <li key={`${error.field}-${index}`}>
              {error.field.length > 0 && (
                <>
                  <Code>{error.field}</Code>:{' '}
                </>
              )}
              {error.detail}
            </li>
          ))}
        </ul>
      )}
      {guidance !== undefined && <p>{guidance}</p>}
      {children}
      {(userCode !== undefined || problem.correlationId !== undefined) && (
        <p className="corr">
          {userCode !== undefined && (
            <>
              Hata kodu: <Code>{userCode}</Code>
              {problem.correlationId !== undefined && ' · '}
            </>
          )}
          {problem.correlationId !== undefined && (
            <>
              Korelasyon kimliği: <Code>{problem.correlationId}</Code>
            </>
          )}
        </p>
      )}
      {(onRetry !== undefined || (userCode !== undefined && assist !== null)) && (
        <div className="actions">
          {onRetry !== undefined && (
            <Button onClick={onRetry} pending={retryPending} pendingLabel="Yeniden deneniyor…">
              {retryLabel}
            </Button>
          )}
          <ErrorCodeHelp userCode={userCode} />
        </div>
      )}
    </div>
  );
}

/**
 * "Help for this error": opens Kaynaklı Yardım with the short error code as the question. Every user
 * code (STK-409, SIP-412, MKB-409, REF-409, YTK-403) has a knowledge-base article behind it.
 */
export function ErrorCodeHelp({ userCode }: { userCode: string | undefined }) {
  const assist = useAssistOptional();
  if (userCode === undefined || assist === null) {
    return null;
  }

  return (
    <Button icon="assist" onClick={() => assist.open({ prefill: `${userCode} ne anlama geliyor?` })}>
      {userCode} kodunu Kaynaklı Yardım’a sor
    </Button>
  );
}

interface StaleAlertProps {
  /** "sipariş", "talep", "belge" … (accusative-free noun used in the heading). */
  what: string;
  onReload: () => void;
  pending?: boolean;
  keepsInput?: boolean;
  problem?: ApiProblem | undefined;
}

/** 412: the document changed after it was rendered. The user's unsent input stays in the form. */
export function StaleAlert({ what, onReload, pending = false, keepsInput = true, problem }: StaleAlertProps) {
  return (
    <div className="alert alert--warn" role="alert">
      <Icon name="warn" />
      <h2>Bu {what} siz görüntüledikten sonra değişti</h2>
      <p>
        İşleminiz uygulanmadı. Güncel {what} bilgisini yükleyin
        {keepsInput ? '; girdiğiniz değerler korunur ve işlemi yeniden gönderebilirsiniz.' : ' ve işlemi yeniden deneyin.'}
      </p>
      {problem !== undefined && (problem.userCode !== undefined || problem.correlationId !== undefined) && (
        <p className="corr">
          {problem.userCode !== undefined && (
            <>
              Hata kodu: <Code>{problem.userCode}</Code>
              {problem.correlationId !== undefined && ' · '}
            </>
          )}
          {problem.correlationId !== undefined && (
            <>
              Korelasyon kimliği: <Code>{problem.correlationId}</Code>
            </>
          )}
        </p>
      )}
      <div className="actions">
        <Button variant="primary" onClick={onReload} pending={pending} pendingLabel="Yükleniyor…">
          Güncel {what} bilgisini yükle
        </Button>
        <ErrorCodeHelp userCode={problem?.userCode} />
      </div>
    </div>
  );
}

/** Result of a command, shown only after the server confirmed it. */
export function SuccessNotice({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="alert alert--ok">
      <Icon name="done" />
      <h2>{title}</h2>
      {children}
    </div>
  );
}

export function InfoNote({ title, children, tone = 'info' }: { title: string; children?: ReactNode; tone?: 'info' | 'warn' }) {
  return (
    <div className={`alert alert--${tone}`}>
      <Icon name={tone === 'warn' ? 'warn' : 'info'} />
      <h2>{title}</h2>
      {children}
    </div>
  );
}

interface QueryViewProps<T> {
  query: UseQueryResult<T, Error>;
  loadingLabel: string;
  /** Noun for the not-found state ("Sipariş", "Talep"). */
  what?: string;
  notFoundAction?: ReactNode;
  children: (data: T) => ReactNode;
}

/** Loading → skeleton; 404 → not found / no access; 403 → not allowed; other failures → details + retry. */
export function QueryView<T>({ query, loadingLabel, what, notFoundAction, children }: QueryViewProps<T>) {
  // A document screen (one that names its subject) renders its page head only once the data is there;
  // until then these states name the browser tab. List screens keep their own page head throughout.
  const ownsTitle = what !== undefined;
  if (query.data !== undefined) {
    return <>{children(query.data)}</>;
  }

  if (query.isError) {
    const problem = asProblem(query.error);
    if (problem.isNotFound) {
      return (
        <>
          {ownsTitle && <DocumentTitle title={`${what ?? 'Kayıt'} bulunamadı`} />}
          <NotFoundState {...(what !== undefined ? { what } : {})} action={notFoundAction} />
        </>
      );
    }

    if (problem.isForbidden) {
      return (
        <>
          {ownsTitle && <DocumentTitle title="Yetkiniz yok" />}
          <ForbiddenState problem={problem} action={notFoundAction} />
        </>
      );
    }

    return (
      <>
        {ownsTitle && <DocumentTitle title="Veri yüklenemedi" />}
        <ProblemAlert problem={problem} title="Veri yüklenemedi" onRetry={() => void query.refetch()} retryPending={query.isFetching} />
      </>
    );
  }

  return (
    <>
      {ownsTitle && <DocumentTitle title={loadingLabel} />}
      <LoadingRegion label={loadingLabel} />
    </>
  );
}
