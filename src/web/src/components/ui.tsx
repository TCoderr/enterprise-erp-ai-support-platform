import { useEffect } from 'react';
import type { ButtonHTMLAttributes, MouseEvent, ReactNode, Ref } from 'react';
import type { Glyph, StatusLook, Tone } from '../lib/labels';
import { formatQuantity } from '../lib/format';
import { Icon } from './Icon';
import type { IconName } from './Icon';

// ---------------------------------------------------------------- status tag, plate, code
interface TagProps {
  tone?: Tone;
  glyph?: Glyph | undefined;
  children: ReactNode;
}

export function Tag({ tone = 'neutral', glyph, children }: TagProps) {
  return (
    <span className={tone === 'neutral' ? 'tag' : `tag tag--${tone}`}>
      {glyph !== undefined && <Icon name={glyph} />}
      {children}
    </span>
  );
}

/** Document state as glyph + word; colour is never the only signal. */
export function StatusTag({ look, note }: { look: StatusLook; note?: string | undefined }) {
  return (
    <Tag tone={look.tone} glyph={look.glyph}>
      {look.label}
      {note !== undefined && <span> · {note}</span>}
    </Tag>
  );
}

/** Identifiers that behave like address plates: document numbers, SKUs, references, error codes. */
export function Code({ children, muted = false }: { children: ReactNode; muted?: boolean }) {
  return (
    <span className={muted ? 'code muted' : 'code'} translate="no">
      {children}
    </span>
  );
}

export function Plate({ children }: { children: ReactNode }) {
  return (
    <span className="plate" translate="no">
      {children}
    </span>
  );
}

/** A quantity; zero is de-emphasised so the non-zero figures carry the row. */
export function Qty({ value, strong = false }: { value: number; strong?: boolean }) {
  const text = formatQuantity(value);
  if (strong) {
    return <strong>{text}</strong>;
  }

  return value === 0 ? <span className="zero">{text}</span> : <>{text}</>;
}

// ---------------------------------------------------------------- buttons
interface ButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'className'> {
  ref?: Ref<HTMLButtonElement>;
  variant?: 'default' | 'primary' | 'danger';
  size?: 'default' | 'small';
  /** A request is in flight: the label switches to `pendingLabel` and further clicks are ignored. */
  pending?: boolean;
  pendingLabel?: string;
  icon?: IconName;
}

/**
 * The one button shape of the product. While pending it stays focusable (aria-disabled, not
 * disabled) so keyboard focus is not lost when a request starts.
 */
export function Button({
  variant = 'default',
  size = 'default',
  pending = false,
  pendingLabel,
  icon,
  type = 'button',
  onClick,
  children,
  ...rest
}: ButtonProps) {
  const classes = ['btn'];
  if (variant !== 'default') {
    classes.push(`btn--${variant}`);
  }

  if (size === 'small') {
    classes.push('btn--small');
  }

  const handleClick = (event: MouseEvent<HTMLButtonElement>) => {
    if (pending) {
      event.preventDefault();
      return;
    }

    onClick?.(event);
  };

  return (
    <button
      {...rest}
      type={type}
      className={classes.join(' ')}
      aria-busy={pending || undefined}
      aria-disabled={pending || rest['aria-disabled'] || undefined}
      onClick={handleClick}
    >
      {icon !== undefined && <Icon name={icon} />}
      {pending && pendingLabel !== undefined ? pendingLabel : children}
    </button>
  );
}

// ---------------------------------------------------------------- page head
interface PageHeadProps {
  title: string;
  /** Document number shown as a plate before the title. */
  plate?: string | undefined;
  /** Overrides the browser tab title (defaults to plate + title). */
  documentTitle?: string | undefined;
  sub?: ReactNode;
  lead?: ReactNode;
  meta?: ReactNode;
  actions?: ReactNode;
}

export function useDocumentTitle(title: string): void {
  useEffect(() => {
    document.title = `${title} — Kantar`;
  }, [title]);
}

/** Sets the tab title for states that have no page head yet (loading, not found, failed). */
export function DocumentTitle({ title }: { title: string }) {
  useDocumentTitle(title);
  return null;
}

export function PageHead({ title, plate, documentTitle, sub, lead, meta, actions }: PageHeadProps) {
  useDocumentTitle(documentTitle ?? (plate !== undefined ? `${plate} · ${title}` : title));
  return (
    <header className="pagehead">
      <div className="pagehead__main">
        <h1>
          {plate !== undefined && <Plate>{plate}</Plate>}
          <span>{title}</span>
        </h1>
        {sub !== undefined && <div className="pagehead__sub">{sub}</div>}
        {lead !== undefined && <p className="pagehead__lead">{lead}</p>}
        {meta}
      </div>
      {actions !== undefined && <div className="actions">{actions}</div>}
    </header>
  );
}

export interface MetaItem {
  label: string;
  value: ReactNode;
}

export function Meta({ items, flush = false }: { items: MetaItem[]; flush?: boolean }) {
  return (
    <dl className={flush ? 'meta meta--flush' : 'meta'}>
      {items.map((item) => (
        <div key={item.label}>
          <dt>{item.label}</dt>
          <dd>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

// ---------------------------------------------------------------- flow steps
export interface FlowStep {
  label: string;
  state: 'done' | 'current' | 'pending';
  glyph?: Glyph;
}

const STEP_TEXT = { done: '(tamamlandı)', current: '(geçerli adım)', pending: '(bekliyor)' } as const;

/** The document's life cycle as an ordered list; state is given by glyph, weight and hidden text. */
export function FlowSteps({ label, steps }: { label: string; steps: FlowStep[] }) {
  return (
    <ol className="flow" aria-label={label}>
      {steps.map((step) => (
        <li
          key={step.label}
          data-step={step.state === 'done' ? 'done' : undefined}
          aria-current={step.state === 'current' ? 'step' : undefined}
        >
          <Icon name={step.glyph ?? (step.state === 'done' ? 'done' : step.state === 'current' ? 'full' : 'draft')} />
          <span className="flow__label">{step.label}</span>
          <span className="sr-only">{STEP_TEXT[step.state]}</span>
        </li>
      ))}
    </ol>
  );
}

/**
 * Builds the steps of a linear life cycle from the position of the current status. For a cancelled
 * document pass `stoppedAt`: the steps it reached are shown as done and none is current — the
 * cancellation itself is reported next to the flow, outside it.
 */
export function linearSteps<S extends string>(
  order: readonly S[],
  labels: Record<S, StatusLook>,
  current: S,
  stoppedAt?: S,
): FlowStep[] {
  const position = order.indexOf(stoppedAt ?? current);
  return order.map((status, index) => {
    const look = labels[status];
    const isLast = index === order.length - 1;
    if (index < position || (index === position && (isLast || stoppedAt !== undefined))) {
      return { label: look.label, state: 'done' as const };
    }

    if (index === position) {
      return { label: look.label, state: 'current' as const, glyph: look.glyph };
    }

    return { label: look.label, state: 'pending' as const };
  });
}
