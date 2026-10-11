import { useEffect, useRef, useState } from 'react';
import type { FormEvent, InputHTMLAttributes, MouseEvent, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react';
import type { ApiProblem } from '../api/problem';
import { Icon } from './Icon';
import { Button } from './ui';

/** DOM id of a form control, derived from the API field path ("lines[1].quantity" → "f-lines-1-quantity"). */
export function fieldDomId(formId: string, field: string): string {
  return `${formId}-${field.replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '')}`;
}

/** Field path → message, from the `errors` array of a problem response. */
export function fieldErrorsOf(problem: ApiProblem | null): Record<string, string> {
  const map: Record<string, string> = {};
  if (problem === null) {
    return map;
  }

  for (const error of problem.errors) {
    if (error.field.length > 0 && map[error.field] === undefined) {
      map[error.field] = error.detail.length > 0 ? error.detail : problem.title;
    }
  }

  return map;
}

function FieldError({ id, message }: { id: string; message: string }) {
  return (
    <span className="field-error" id={id}>
      <Icon name="error" />
      {message}
    </span>
  );
}

interface FieldShell {
  formId: string;
  /** API field name; also the control's `name`. */
  name: string;
  label: string;
  /** Visually hide the label (table cells, where the column header already names the value). */
  labelHidden?: boolean | undefined;
  hint?: ReactNode;
  error?: string | undefined;
  fieldClassName?: string | undefined;
}

function describedBy(id: string, hint: ReactNode, error: string | undefined): string | undefined {
  const ids = [error !== undefined ? `${id}-hata` : null, hint !== undefined ? `${id}-ipucu` : null].filter((v) => v !== null);
  return ids.length > 0 ? ids.join(' ') : undefined;
}

type TextFieldProps = FieldShell &
  Omit<InputHTMLAttributes<HTMLInputElement>, 'id' | 'name' | 'value' | 'onChange' | 'className'> & {
    value: string;
    onChange: (value: string) => void;
    inputClassName?: string;
  };

export function TextField({
  formId,
  name,
  label,
  labelHidden = false,
  hint,
  error,
  fieldClassName,
  value,
  onChange,
  inputClassName,
  type = 'text',
  autoComplete = 'off',
  ...rest
}: TextFieldProps) {
  const id = fieldDomId(formId, name);
  return (
    <div className={fieldClassName !== undefined ? `field ${fieldClassName}` : 'field'}>
      <label htmlFor={id} className={labelHidden ? 'sr-only' : undefined}>
        {label}
      </label>
      <input
        {...rest}
        id={id}
        name={name}
        type={type}
        autoComplete={autoComplete}
        className={inputClassName !== undefined ? `input ${inputClassName}` : 'input'}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-invalid={error !== undefined || undefined}
        aria-describedby={describedBy(id, hint, error)}
      />
      {error !== undefined && <FieldError id={`${id}-hata`} message={error} />}
      {hint !== undefined && (
        <span className="hint" id={`${id}-ipucu`}>
          {hint}
        </span>
      )}
    </div>
  );
}

type TextAreaFieldProps = FieldShell &
  Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, 'id' | 'name' | 'value' | 'onChange' | 'className'> & {
    value: string;
    onChange: (value: string) => void;
    /** Shows "n / max" under the field. */
    counter?: boolean;
  };

export function TextAreaField({
  formId,
  name,
  label,
  labelHidden = false,
  hint,
  error,
  fieldClassName,
  value,
  onChange,
  counter = false,
  rows = 4,
  autoComplete = 'off',
  ...rest
}: TextAreaFieldProps) {
  const id = fieldDomId(formId, name);
  return (
    <div className={fieldClassName !== undefined ? `field ${fieldClassName}` : 'field'}>
      <label htmlFor={id} className={labelHidden ? 'sr-only' : undefined}>
        {label}
      </label>
      <textarea
        {...rest}
        id={id}
        name={name}
        rows={rows}
        autoComplete={autoComplete}
        className="textarea"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-invalid={error !== undefined || undefined}
        aria-describedby={describedBy(id, hint, error)}
      />
      {counter && rest.maxLength !== undefined && (
        <span className="counter">
          {value.length} / {rest.maxLength}
        </span>
      )}
      {error !== undefined && <FieldError id={`${id}-hata`} message={error} />}
      {hint !== undefined && (
        <span className="hint" id={`${id}-ipucu`}>
          {hint}
        </span>
      )}
    </div>
  );
}

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

type SelectFieldProps = FieldShell &
  Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id' | 'name' | 'value' | 'onChange' | 'className'> & {
    value: string;
    onChange: (value: string) => void;
    options: readonly SelectOption[];
    /** Text of the empty first option; omit when a value is always selected. */
    placeholder?: string;
  };

export function SelectField({
  formId,
  name,
  label,
  labelHidden = false,
  hint,
  error,
  fieldClassName,
  value,
  onChange,
  options,
  placeholder,
  ...rest
}: SelectFieldProps) {
  const id = fieldDomId(formId, name);
  return (
    <div className={fieldClassName !== undefined ? `field ${fieldClassName}` : 'field'}>
      <label htmlFor={id} className={labelHidden ? 'sr-only' : undefined}>
        {label}
      </label>
      <select
        {...rest}
        id={id}
        name={name}
        className="select"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-invalid={error !== undefined || undefined}
        aria-describedby={describedBy(id, hint, error)}
      >
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((option) => (
          <option key={option.value} value={option.value} disabled={option.disabled}>
            {option.label}
          </option>
        ))}
      </select>
      {error !== undefined && <FieldError id={`${id}-hata`} message={error} />}
      {hint !== undefined && (
        <span className="hint" id={`${id}-ipucu`}>
          {hint}
        </span>
      )}
    </div>
  );
}

interface CheckFieldProps {
  formId: string;
  name: string;
  label: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

export function CheckField({ formId, name, label, checked, onChange, disabled = false }: CheckFieldProps) {
  const id = fieldDomId(formId, name);
  return (
    <label className="check" htmlFor={id}>
      <input id={id} name={name} type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} />
      <span>{label}</span>
    </label>
  );
}

// ---------------------------------------------------------------- error summary
export interface SummaryItem {
  /** DOM id of the control to jump to; omit for errors that belong to no single field. */
  fieldId?: string | undefined;
  message: string;
}

interface FormErrorSummaryProps {
  title: string;
  items: SummaryItem[];
  note?: ReactNode;
  /** Changes on every failed submit so that focus moves here each time. */
  focusKey: number;
  children?: ReactNode;
}

/**
 * Shown at the top of a form after a failed submit. It takes focus, so the failure is announced and
 * keyboard users land on the list; each entry jumps to its field.
 */
export function FormErrorSummary({ title, items, note, focusKey, children }: FormErrorSummaryProps) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, [focusKey]);

  const jump = (event: MouseEvent<HTMLAnchorElement>, fieldId: string) => {
    const target = document.getElementById(fieldId);
    if (target !== null) {
      event.preventDefault();
      target.focus();
    }
  };

  return (
    <div className="alert alert--danger" ref={ref} tabIndex={-1} role="group" aria-label={title}>
      <Icon name="error" />
      <h2>{title}</h2>
      {items.length > 0 && (
        <ul>
          {items.map((item, index) => (
            <li key={`${item.fieldId ?? 'genel'}-${index}`}>
              {item.fieldId !== undefined ? (
                <a href={`#${item.fieldId}`} onClick={(event) => jump(event, item.fieldId as string)}>
                  {item.message}
                </a>
              ) : (
                item.message
              )}
            </li>
          ))}
        </ul>
      )}
      {note !== undefined && <p>{note}</p>}
      {children}
    </div>
  );
}

/** Region whose changes are announced politely; it must exist before its content appears. */
export function LiveRegion({ children }: { children?: ReactNode }) {
  return <div aria-live="polite">{children}</div>;
}

// ---------------------------------------------------------------- inline confirmation
export interface Disclosure {
  open: boolean;
  panelId: string;
  show: () => void;
  /** Closes the step and returns focus to the control that opened it. */
  hide: () => void;
  toggle: () => void;
}

export function useDisclosure(panelId: string): Disclosure {
  const [open, setOpen] = useState(false);
  // The control that had focus when the step opened (its trigger); focus goes back there on close.
  const opener = useRef<HTMLElement | null>(null);
  const show = () => {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setOpen(true);
  };
  const hide = () => {
    setOpen(false);
    const target = opener.current;
    opener.current = null;
    if (target !== null && target.isConnected) {
      target.focus();
    }
  };
  return { open, panelId, show, hide, toggle: () => (open ? hide() : show()) };
}

interface ConfirmStepProps {
  disclosure: Disclosure;
  /** What exactly will happen, in a sentence. */
  question: ReactNode;
  confirmLabel: string;
  pendingLabel: string;
  tone?: 'danger' | 'primary';
  pending?: boolean;
  onConfirm: () => void;
  children?: ReactNode;
}

/**
 * The explicit second step of a destructive or consequential action. Inline, not a modal: the
 * document stays readable while the user decides. Focus moves into the step when it opens.
 */
export function ConfirmStep({ disclosure, question, confirmLabel, pendingLabel, tone = 'danger', pending = false, onConfirm, children }: ConfirmStepProps) {
  const ref = useRef<HTMLFormElement>(null);
  const { open } = disclosure;
  useEffect(() => {
    if (open) {
      const first = ref.current?.querySelector<HTMLElement>('input, textarea, select, button');
      first?.focus();
    }
  }, [open]);

  if (!open) {
    return null;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onConfirm();
  };

  return (
    <form className="confirm" id={disclosure.panelId} ref={ref} onSubmit={submit} noValidate>
      <p>{question}</p>
      {children}
      <div className="actions">
        <Button onClick={disclosure.hide}>Vazgeç</Button>
        <Button type="submit" variant={tone} pending={pending} pendingLabel={pendingLabel}>
          {confirmLabel}
        </Button>
      </div>
    </form>
  );
}
