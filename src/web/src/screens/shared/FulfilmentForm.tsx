import { useState } from 'react';
import type { FormEvent } from 'react';
import { purchasingApi, salesApi } from '../../api/endpoints';
import type { FulfilmentInputLine } from '../../api/endpoints';
import type { ApiProblem } from '../../api/problem';
import type { Receipt, Shipment } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { FormErrorSummary, TextField, fieldDomId } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { Icon } from '../../components/Icon';
import { ErrorCodeHelp, ProblemAlert, StaleAlert } from '../../components/states';
import { Button, Code, Qty, Tag } from '../../components/ui';
import { formatQuantity, parseQuantity } from '../../lib/format';
import { useCommand } from '../../lib/useCommand';

export interface FulfilmentRow {
  id: string;
  sku: string;
  productName: string;
  unit: string;
  orderedQty: number;
  /** Shipped or received so far. */
  doneQty: number;
  openQty: number;
}

interface Wording {
  formId: string;
  title: string;
  referenceField: 'dispatchReference' | 'externalReference';
  referenceLabel: string;
  referenceHint: string;
  doneHeader: string;
  thisHeader: string;
  lineLabel: (sku: string) => string;
  submit: string;
  pending: string;
  failed: string;
  nothingApplied: string;
  allDone: string;
  emptyNote: string;
}

const WORDING: Record<'shipment' | 'receipt', Wording> = {
  shipment: {
    formId: 'sevk',
    title: 'Sevkiyat girişi',
    referenceField: 'dispatchReference',
    referenceLabel: 'Sevk referansı (irsaliye no)',
    referenceHint: '3–48 karakter; A–Z, 0–9 ve - _ / . kullanılabilir. Aynı referans bu sipariş için ikinci kez kaydedilemez.',
    doneHeader: 'Sevk edilen',
    thisHeader: 'Bu sevkiyat',
    lineLabel: (sku) => `${sku} için bu sevkiyatın miktarı`,
    submit: 'Sevkiyatı kaydet',
    pending: 'Sevkiyat kaydediliyor…',
    failed: 'Sevkiyat kaydedilmedi',
    nothingApplied: 'Hiçbir satır kaydedilmedi; stok ve rezervasyon değişmedi.',
    allDone: 'Tamamı sevk edildi',
    emptyNote: 'Boş bırakılan satır bu sevkiyata dahil edilmez. Kalan miktar siparişte açık kalır.',
  },
  receipt: {
    formId: 'kabul',
    title: 'Mal kabul',
    referenceField: 'externalReference',
    referenceLabel: 'Tedarikçi irsaliye numarası',
    referenceHint: '3–48 karakter; A–Z, 0–9 ve - _ / . kullanılabilir. Aynı tedarikçinin aynı irsaliyesi ikinci kez kaydedilemez.',
    doneHeader: 'Kabul edilen',
    thisHeader: 'Bu kabul',
    lineLabel: (sku) => `${sku} için bu kabulün miktarı`,
    submit: 'Mal kabulü kaydet',
    pending: 'Mal kabul kaydediliyor…',
    failed: 'Mal kabul kaydedilmedi',
    nothingApplied: 'Hiçbir satır kaydedilmedi; stok değişmedi.',
    allDone: 'Tamamı kabul edildi',
    emptyNote: 'Boş bırakılan satır bu kabule dahil edilmez. Kalan miktar siparişte açık kalır.',
  },
};

const REFERENCE_PATTERN = /^[A-Z0-9\-_/.]{3,48}$/;

interface Command {
  reference: string;
  lines: FulfilmentInputLine[];
}

interface FulfilmentFormProps {
  kind: 'shipment' | 'receipt';
  orderId: string;
  rows: readonly FulfilmentRow[];
  /** Called with the document the server created. */
  onPosted: (document: Shipment | Receipt) => void | Promise<void>;
  /** Re-reads the order (after a conflict the open quantities on screen are out of date). */
  onReload: () => void;
  reloading?: boolean;
  onCancel?: (() => void) | undefined;
}

/**
 * Dispatch and goods-receipt entry: a business reference plus a quantity per line, each at most the
 * open quantity. Partial entry is normal. No If-Match is sent — the server guards these two
 * commands by the remaining quantity, so two operators do not block each other.
 */
export function FulfilmentForm({ kind, orderId, rows, onPosted, onReload, reloading = false, onCancel }: FulfilmentFormProps) {
  const w = WORDING[kind];
  const [reference, setReference] = useState('');
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [local, setLocal] = useState<Record<string, string>>({});
  const [serverLines, setServerLines] = useState<string[]>([]);
  const [attempt, setAttempt] = useState(0);

  const post = useCommand(
    (command: Command, key: string): Promise<Shipment | Receipt> =>
      kind === 'shipment'
        ? salesApi.ship(orderId, command.reference, command.lines, key)
        : purchasingApi.receive(orderId, command.reference, command.lines, key),
    {
      onSuccess: async (document) => {
        setReference('');
        setQuantities({});
        setLocal({});
        await onPosted(document);
      },
      onProblem: (problem, command) => {
        // Server paths index the lines that were SENT; remember which order line each one was.
        setServerLines(command.lines.map((line) => line.orderLineId));
        setAttempt((n) => n + 1);
        if (problem.code === 'exceeds_remaining' || problem.code === 'invalid_state') {
          onReload();
        }
      },
    },
  );

  const referenceId = fieldDomId(w.formId, w.referenceField);
  const quantityId = (rowId: string) => fieldDomId(w.formId, `q-${rowId}`);

  // Server field errors, mapped back onto the reference field and the order lines.
  const serverErrors: Record<string, string> = {};
  const unmapped: ApiProblem['errors'][number][] = [];
  if (post.problem !== null) {
    for (const error of post.problem.errors) {
      const line = /^lines\[(\d+)\]/.exec(error.field);
      const rowId = line ? serverLines[Number(line[1])] : undefined;
      if (error.field === w.referenceField) {
        serverErrors[w.referenceField] = error.detail;
      } else if (rowId !== undefined) {
        serverErrors[rowId] = error.detail;
      } else {
        unmapped.push(error);
      }
    }

    if (post.problem.code === 'duplicate_business_reference') {
      serverErrors[w.referenceField] = 'Bu referans daha önce kaydedildi.';
    }
  }

  const errorOf = (key: string): string | undefined => local[key] ?? serverErrors[key];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const normalizedReference = reference.trim().toLocaleUpperCase('en-US');
    if (normalizedReference.length === 0) {
      next[w.referenceField] = `${w.referenceLabel} zorunludur.`;
    } else if (!REFERENCE_PATTERN.test(normalizedReference)) {
      next[w.referenceField] = '3–48 karakter; yalnız A–Z, 0–9 ve - _ / . kullanılabilir.';
    }

    const lines: FulfilmentInputLine[] = [];
    for (const row of rows) {
      const text = (quantities[row.id] ?? '').trim();
      if (text.length === 0 || row.openQty === 0) {
        continue;
      }

      const quantity = parseQuantity(text);
      if (quantity === null || quantity < 1) {
        next[row.id] = 'En az 1 olan bir tam sayı girin ya da satırı boş bırakın.';
      } else if (quantity > row.openQty) {
        next[row.id] = `En fazla ${formatQuantity(row.openQty)} girilebilir.`;
      } else {
        lines.push({ orderLineId: row.id, quantity });
      }
    }

    if (lines.length === 0 && Object.keys(next).every((key) => key === w.referenceField)) {
      next.lines = 'En az bir satıra miktar girin.';
    }

    setLocal(next);
    post.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    void post.run({ reference: normalizedReference, lines });
  };

  const fillAll = () => {
    const next: Record<string, string> = {};
    for (const row of rows) {
      if (row.openQty > 0) {
        next[row.id] = String(row.openQty);
      }
    }

    setQuantities(next);
    setLocal({});
  };

  const summary: SummaryItem[] = [];
  const referenceError = errorOf(w.referenceField);
  if (referenceError !== undefined) {
    summary.push({ fieldId: referenceId, message: `${w.referenceLabel}: ${referenceError}` });
  }

  for (const row of rows) {
    const message = errorOf(row.id);
    if (message !== undefined) {
      summary.push({ fieldId: quantityId(row.id), message: `${row.sku} ${row.productName}: ${message}` });
    }
  }

  if (local.lines !== undefined) {
    const firstOpen = rows.find((row) => row.openQty > 0);
    summary.push({ fieldId: firstOpen ? quantityId(firstOpen.id) : undefined, message: local.lines });
  }

  for (const error of unmapped) {
    summary.push({ message: error.detail.length > 0 ? error.detail : error.code });
  }

  const problem = post.problem;
  const hasLocal = Object.keys(local).length > 0;
  const columns: Column<FulfilmentRow>[] = [
    { id: 'sku', header: 'SKU', kind: 'code', rowHeader: true, cell: (row) => <span translate="no">{row.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (row) => row.productName },
    { id: 'unit', header: 'Birim', cell: (row) => row.unit },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (row) => <Qty value={row.orderedQty} /> },
    { id: 'done', header: w.doneHeader, kind: 'num', cell: (row) => <Qty value={row.doneQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (row) => <Qty value={row.openQty} /> },
    {
      id: 'this',
      header: w.thisHeader,
      kind: 'num',
      cell: (row) => {
        if (row.openQty === 0) {
          return (
            <Tag tone="ok" glyph="done">
              {w.allDone}
            </Tag>
          );
        }

        const id = quantityId(row.id);
        const message = errorOf(row.id);
        return (
          <span className="cell-stack">
            <label className="sr-only" htmlFor={id}>
              {w.lineLabel(row.sku)}
            </label>
            <input
              id={id}
              name={`quantity-${row.sku}`}
              className="input input--qty"
              type="text"
              inputMode="numeric"
              pattern="[0-9]*"
              autoComplete="off"
              value={quantities[row.id] ?? ''}
              onChange={(event) => {
                const { value } = event.target;
                setQuantities((previous) => ({ ...previous, [row.id]: value }));
              }}
              aria-invalid={message !== undefined || undefined}
              aria-describedby={message !== undefined ? `${id}-hata` : undefined}
            />
            {message !== undefined && (
              <span className="field-error" id={`${id}-hata`}>
                <Icon name="error" />
                {message}
              </span>
            )}
          </span>
        );
      },
    },
  ];

  return (
    <form className="region" onSubmit={submit} noValidate aria-labelledby={`${w.formId}-baslik`}>
      <div className="region--pad stack">
        <h2 className="section-h section-h--flush" id={`${w.formId}-baslik`}>
          {w.title}
        </h2>

        {problem !== null && problem.isStale && <StaleAlert what="sipariş" onReload={onReload} pending={reloading} problem={problem} />}
        {problem !== null && !problem.isStale && summary.length === 0 && (
          <ProblemAlert
            problem={problem}
            title={w.failed}
            onRetry={problem.isRetryable ? () => document.getElementById(`${w.formId}-gonder`)?.click() : undefined}
          >
            {!problem.isRetryable && <p>{w.nothingApplied}</p>}
          </ProblemAlert>
        )}
        {summary.length > 0 && (hasLocal || problem !== null) && (
          <FormErrorSummary
            title={`${problem !== null && !hasLocal ? problem.title : w.failed}: ${summary.length} alanı düzeltin`}
            items={summary}
            focusKey={attempt}
            note={w.nothingApplied}
          >
            {problem !== null && !hasLocal && (
              <>
                {problem.code === 'duplicate_business_reference' && problem.detail !== undefined && <p>{problem.detail}</p>}
                {problem.code === 'exceeds_remaining' && (
                  <p>Açık miktarlar başka bir kayıtla azalmış olabilir; tablo güncel miktarlarla yenilendi.</p>
                )}
                {(problem.userCode !== undefined || problem.correlationId !== undefined) && (
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
                {problem.userCode !== undefined && (
                  <div className="actions">
                    <ErrorCodeHelp userCode={problem.userCode} />
                  </div>
                )}
              </>
            )}
          </FormErrorSummary>
        )}

        <TextField
          formId={w.formId}
          name={w.referenceField}
          label={w.referenceLabel}
          value={reference}
          onChange={setReference}
          inputClassName="code"
          fieldClassName="measure-form"
          spellCheck={false}
          autoCapitalize="characters"
          maxLength={48}
          required
          translate="no"
          error={referenceError}
          hint={w.referenceHint}
        />
        <div className="actions">
          <Button onClick={fillAll}>Açık miktarların tamamını doldur</Button>
        </div>
      </div>

      <DataTable caption={`${w.title}: satırlar`} captionHidden columns={columns} rows={rows} rowKey={(row) => row.id} />

      {/* The note stays in the flow; the sticky bar on narrow screens holds only the decision. */}
      <p className="region--pad small actions--ruled">{w.emptyNote}</p>
      <div className="region--pad actions actions--sticky actions--end">
        {onCancel !== undefined && <Button onClick={onCancel}>Vazgeç</Button>}
        <Button id={`${w.formId}-gonder`} type="submit" variant="primary" pending={post.pending} pendingLabel={w.pending}>
          {w.submit}
        </Button>
      </div>
    </form>
  );
}
