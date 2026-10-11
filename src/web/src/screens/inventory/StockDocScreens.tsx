import { useState } from 'react';
import type { FormEvent } from 'react';
import { inventoryApi } from '../../api/endpoints';
import type { ApiProblem } from '../../api/problem';
import type { Adjustment, AdjustmentReason, Transfer } from '../../api/types';
import { warehousesWith } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, ConfirmStep, FormErrorSummary, LiveRegion, SelectField, TextAreaField, TextField, fieldDomId, useDisclosure } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { Icon } from '../../components/Icon';
import { ProductPicker } from '../../components/ProductPicker';
import type { PickedProduct } from '../../components/ProductPicker';
import { EmptyState, InfoNote, ProblemAlert, SuccessNotice } from '../../components/states';
import { Button, Code, PageHead } from '../../components/ui';
import { formatDateTime, formatQuantity, formatSigned, parseQuantity, parseSignedQuantity } from '../../lib/format';
import { ADJUSTMENT_REASON_LABELS, labelOf, personLabel } from '../../lib/labels';
import { ListRegion, useCursorList } from '../../lib/lists';
import { useInvalidate } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { useWarehouse } from '../../shell/context';
import { NoWarehouse, WarehouseTag } from './StockScreens';

const REFERENCE_PATTERN = /^[A-Z0-9\-_/.]{3,48}$/;
const MAX_LINES = 50;
const MAX_QUANTITY = 100_000;

interface StockLine {
  productId: string;
  sku: string;
  name: string;
  unit: string;
  onHand: number;
  reserved: number;
  available: number;
  value: string;
}

function toStockLine(product: PickedProduct): StockLine {
  return {
    productId: product.productId,
    sku: product.sku,
    name: product.name,
    unit: product.unit,
    onHand: product.onHand ?? 0,
    reserved: product.reserved ?? 0,
    available: product.available ?? 0,
    value: '',
  };
}

interface StockLinesProps {
  formId: string;
  mode: 'transfer' | 'adjustment';
  lines: StockLine[];
  errors: Record<string, string | undefined>;
  onChange: (productId: string, value: string) => void;
  onRemove: (productId: string) => void;
}

/** Line editor of the two single-step stock documents: quantity to move, or signed quantity to adjust. */
function StockLines({ formId, mode, lines, errors, onChange, onRemove }: StockLinesProps) {
  const header = mode === 'transfer' ? 'Transfer miktarı' : 'Düzeltme (+/−)';
  return (
    <table className="stacked" role="table">
      <caption className="sr-only">Belge satırları</caption>
      <thead role="rowgroup">
        <tr role="row">
          <th scope="col" role="columnheader">
            SKU
          </th>
          <th scope="col" role="columnheader">
            Ürün
          </th>
          <th scope="col" role="columnheader">
            Birim
          </th>
          <th scope="col" role="columnheader" className="num">
            Eldeki
          </th>
          <th scope="col" role="columnheader" className="num">
            Rezerve
          </th>
          <th scope="col" role="columnheader" className="num">
            Kullanılabilir
          </th>
          <th scope="col" role="columnheader" className="num">
            {header}
          </th>
          <th scope="col" role="columnheader">
            <span className="sr-only">İşlem</span>
          </th>
        </tr>
      </thead>
      <tbody role="rowgroup">
        {lines.map((line) => {
          const id = fieldDomId(formId, `satir-${line.productId}`);
          const error = errors[line.productId];
          return (
            <tr key={line.productId} role="row">
              <th scope="row" role="rowheader" className="code" translate="no">
                {line.sku}
              </th>
              <td role="cell" className="name">
                {line.name}
              </td>
              <td role="cell" data-label="Birim">
                {line.unit}
              </td>
              <td role="cell" className="num" data-label="Eldeki">
                {formatQuantity(line.onHand)}
              </td>
              <td role="cell" className="num" data-label="Rezerve">
                {formatQuantity(line.reserved)}
              </td>
              <td role="cell" className="num" data-label="Kullanılabilir">
                <strong>{formatQuantity(line.available)}</strong>
              </td>
              <td role="cell" className="num" data-label={header}>
                <span className="cell-stack">
                  <label className="sr-only" htmlFor={id}>
                    {line.sku} {mode === 'transfer' ? 'transfer miktarı' : 'düzeltme miktarı (artı veya eksi)'}
                  </label>
                  <input
                    id={id}
                    name={`value-${line.sku}`}
                    className="input input--qty"
                    type="text"
                    inputMode={mode === 'transfer' ? 'numeric' : 'text'}
                    autoComplete="off"
                    placeholder={mode === 'transfer' ? undefined : '+5 veya -3'}
                    value={line.value}
                    onChange={(event) => onChange(line.productId, event.target.value)}
                    aria-invalid={error !== undefined || undefined}
                    aria-describedby={error !== undefined ? `${id}-hata` : undefined}
                  />
                  {error !== undefined && (
                    <span className="field-error" id={`${id}-hata`}>
                      <Icon name="error" />
                      {error}
                    </span>
                  )}
                </span>
              </td>
              <td role="cell" data-label="İşlem">
                <button type="button" className="btn btn--small" aria-label={`${line.sku} satırını kaldır`} onClick={() => onRemove(line.productId)}>
                  <Icon name="trash" />
                </button>
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

/** Maps "lines[2]…" field paths of a problem onto the product ids of the lines that were sent. */
function lineErrors(problem: ApiProblem | null, sent: readonly string[]): { byProduct: Record<string, string>; byField: Record<string, string>; rest: string[] } {
  const byProduct: Record<string, string> = {};
  const byField: Record<string, string> = {};
  const rest: string[] = [];
  if (problem !== null) {
    for (const error of problem.errors) {
      const match = /^lines\[(\d+)\]/.exec(error.field);
      const productId = match ? sent[Number(match[1])] : undefined;
      if (productId !== undefined) {
        byProduct[productId] = error.detail;
      } else if (error.field.length > 0 && !error.field.startsWith('lines[')) {
        byField[error.field] = error.detail;
      } else {
        rest.push(error.detail.length > 0 ? error.detail : error.code);
      }
    }
  }

  return { byProduct, byField, rest };
}

function normalizeReference(text: string): string {
  return text.trim().toLocaleUpperCase('en-US');
}

// ---------------------------------------------------------------- transfer
const TRANSFER_FORM = 'transfer';

interface TransferCommand {
  reference: string;
  fromWarehouseId: string;
  toWarehouseId: string;
  note: string | undefined;
  lines: { productId: string; quantity: number }[];
}

function TransferForm({ onPosted }: { onPosted: (transfer: Transfer) => void | Promise<void> }) {
  const me = useMe();
  const { selected } = useWarehouse();
  const operable = warehousesWith(me, 'Operate');
  const [reference, setReference] = useState('');
  const [fromId, setFromId] = useState(operable.some((w) => w.id === selected?.id) ? (selected?.id ?? '') : (operable[0]?.id ?? ''));
  const [toId, setToId] = useState('');
  const [note, setNote] = useState('');
  const [lines, setLines] = useState<StockLine[]>([]);
  const [local, setLocal] = useState<Record<string, string>>({});
  const [sent, setSent] = useState<string[]>([]);
  const [attempt, setAttempt] = useState(0);

  const post = useCommand((command: TransferCommand, key: string) => inventoryApi.createTransfer(command, key), {
    onSuccess: async (transfer) => {
      setReference('');
      setNote('');
      setLines([]);
      setLocal({});
      await onPosted(transfer);
    },
    onProblem: (_problem, command) => {
      setSent(command.lines.map((l) => l.productId));
      setAttempt((n) => n + 1);
    },
  });

  const server = lineErrors(post.problem, sent);
  const errorOf = (key: string): string | undefined => local[key] ?? server.byField[key];
  const lineError = (productId: string): string | undefined => local[`line:${productId}`] ?? server.byProduct[productId];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const ref = normalizeReference(reference);
    if (!REFERENCE_PATTERN.test(ref)) {
      next.reference = '3–48 karakter; yalnız A–Z, 0–9 ve - _ / . kullanılabilir.';
    }

    if (fromId.length === 0) {
      next.fromWarehouseId = 'Kaynak depoyu seçin.';
    }

    if (toId.length === 0) {
      next.toWarehouseId = 'Hedef depoyu seçin.';
    } else if (toId === fromId) {
      next.toWarehouseId = 'Kaynak ve hedef depo aynı olamaz.';
    }

    if (note.trim().length > 500) {
      next.note = 'Not en fazla 500 karakter olabilir.';
    }

    if (lines.length === 0) {
      next.lines = 'En az bir ürün satırı ekleyin.';
    }

    const payload: TransferCommand['lines'] = [];
    for (const line of lines) {
      const quantity = parseQuantity(line.value);
      if (quantity === null || quantity < 1 || quantity > MAX_QUANTITY) {
        next[`line:${line.productId}`] = `Miktar 1 ile ${formatQuantity(MAX_QUANTITY)} arasında bir tam sayı olmalıdır.`;
      } else if (quantity > line.available) {
        next[`line:${line.productId}`] = `Yalnız kullanılabilir miktar taşınabilir: en fazla ${formatQuantity(line.available)}.`;
      } else {
        payload.push({ productId: line.productId, quantity });
      }
    }

    setLocal(next);
    post.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    const trimmed = note.trim();
    void post.run({ reference: ref, fromWarehouseId: fromId, toWarehouseId: toId, note: trimmed.length > 0 ? trimmed : undefined, lines: payload });
  };

  const summary: SummaryItem[] = [];
  const add = (key: string, label: string, fieldId: string) => {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId, message: `${label}: ${message}` });
    }
  };
  add('reference', 'Transfer referansı', fieldDomId(TRANSFER_FORM, 'reference'));
  add('fromWarehouseId', 'Kaynak depo', fieldDomId(TRANSFER_FORM, 'fromWarehouseId'));
  add('toWarehouseId', 'Hedef depo', fieldDomId(TRANSFER_FORM, 'toWarehouseId'));
  add('note', 'Not', fieldDomId(TRANSFER_FORM, 'note'));
  add('lines', 'Satırlar', fieldDomId(TRANSFER_FORM, 'urun-ara'));
  for (const line of lines) {
    const message = lineError(line.productId);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(TRANSFER_FORM, `satir-${line.productId}`), message: `${line.sku} ${line.name}: ${message}` });
    }
  }

  for (const message of server.rest) {
    summary.push({ message });
  }

  if (operable.length < 2) {
    return (
      <InfoNote tone="warn" title="Transfer için iki depoda işlem yetkisi gerekir">
        <p>
          Transfer, kaynak ve hedef deponun ikisinde de “İşlem” seviyesinde yetki ister. {operable.length === 0 ? 'Hiçbir depoda' : 'Yalnız bir depoda'} bu
          yetkiniz var; yeni transfer kaydedemezsiniz.
        </p>
      </InfoNote>
    );
  }

  const options = operable.map((w) => ({ value: w.id, label: `${w.code} · ${w.name}` }));
  const errors: Record<string, string | undefined> = {};
  for (const line of lines) {
    errors[line.productId] = lineError(line.productId);
  }

  const problem = post.problem;
  return (
    <form className="region" onSubmit={submit} noValidate aria-labelledby="transfer-baslik">
      <div className="region--pad stack">
        <h2 className="section-h section-h--flush" id="transfer-baslik">
          Yeni transfer
        </h2>
        {summary.length > 0 && (
          <FormErrorSummary
            title={`Transfer kaydedilmedi: ${summary.length} alanı düzeltin`}
            items={summary}
            focusKey={attempt}
            note={problem !== null ? `${problem.title}. Hiçbir satır kaydedilmedi; iki depoda da stok değişmedi.` : 'Hiçbir satır kaydedilmedi; stok değişmedi.'}
          />
        )}
        {problem !== null && summary.length === 0 && (
          <ProblemAlert
            problem={problem}
            title="Transfer kaydedilmedi"
            onRetry={problem.isRetryable ? () => document.getElementById('transfer-gonder')?.click() : undefined}
          >
            {!problem.isRetryable && <p>Hiçbir satır kaydedilmedi; iki depoda da stok değişmedi.</p>}
          </ProblemAlert>
        )}

        <div className="formgrid">
          <TextField
            formId={TRANSFER_FORM}
            name="reference"
            label="Transfer referansı"
            value={reference}
            onChange={setReference}
            inputClassName="code"
            spellCheck={false}
            autoCapitalize="characters"
            maxLength={48}
            required
            translate="no"
            error={errorOf('reference')}
            hint="Belgenizdeki numara. Aynı referans ikinci kez kaydedilemez."
          />
          <SelectField
            formId={TRANSFER_FORM}
            name="fromWarehouseId"
            label="Kaynak depo"
            value={fromId}
            onChange={(value) => {
              setFromId(value);
              // Lines carry the availability of the previous source; they no longer apply.
              setLines([]);
            }}
            options={options}
            required
            error={errorOf('fromWarehouseId')}
            hint={lines.length > 0 ? 'Kaynak depoyu değiştirirseniz satırlar temizlenir.' : undefined}
          />
          <SelectField
            formId={TRANSFER_FORM}
            name="toWarehouseId"
            label="Hedef depo"
            value={toId}
            onChange={setToId}
            options={options.map((option) => ({ ...option, disabled: option.value === fromId }))}
            placeholder="Hedef depoyu seçin"
            required
            error={errorOf('toWarehouseId')}
          />
          <TextAreaField
            formId={TRANSFER_FORM}
            name="note"
            label="Not (isteğe bağlı)"
            value={note}
            onChange={setNote}
            rows={2}
            maxLength={500}
            fieldClassName="field--full"
            error={errorOf('note')}
          />
        </div>

        {fromId.length > 0 && (
          <ProductPicker
            formId={TRANSFER_FORM}
            source={{ kind: 'stock', warehouseId: fromId }}
            excluded={new Set(lines.map((l) => l.productId))}
            onPick={(product) => {
              if (lines.length < MAX_LINES) {
                setLines((previous) => [...previous, toStockLine(product)]);
              }
            }}
          />
        )}
      </div>

      {lines.length === 0 ? (
        <p className="region--pad small">Henüz satır yok. Kaynak depodaki bakiyelerden ürün arayıp ekleyin; yalnız kullanılabilir miktar taşınabilir.</p>
      ) : (
        <StockLines
          formId={TRANSFER_FORM}
          mode="transfer"
          lines={lines}
          errors={errors}
          onChange={(productId, value) => setLines((previous) => previous.map((l) => (l.productId === productId ? { ...l, value } : l)))}
          onRemove={(productId) => setLines((previous) => previous.filter((l) => l.productId !== productId))}
        />
      )}

      <p className="region--pad small actions--ruled">Transfer kaydedildiği anda tamamlanır: kaynak depodan düşer, hedef depoya eklenir. Belge sonradan değiştirilemez.</p>
      <div className="region--pad actions actions--sticky actions--end">
        <Button id="transfer-gonder" type="submit" variant="primary" pending={post.pending} pendingLabel="Transfer kaydediliyor…">
          Transferi kaydet
        </Button>
      </div>
    </form>
  );
}

export function TransferScreen() {
  const me = useMe();
  const { selected, nameOf, codeOf } = useWarehouse();
  const invalidate = useInvalidate();
  const [posted, setPosted] = useState<Transfer | null>(null);
  const list = useCursorList<Transfer>(
    ['transfers', selected?.id ?? null],
    (cursor, signal) => inventoryApi.transfers({ warehouseId: selected?.id ?? '', cursor }, signal),
    { enabled: selected !== null },
  );

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title="Transfer" />
        <NoWarehouse />
      </div>
    );
  }

  const depot = (warehouseId: string) => (
    <>
      {codeOf(warehouseId) !== undefined && <Code>{codeOf(warehouseId)}</Code>} {nameOf(warehouseId)}
    </>
  );
  const columns: Column<Transfer>[] = [
    { id: 'number', header: 'Transfer no', kind: 'code', rowHeader: true, cell: (t) => <span translate="no">{t.number}</span> },
    { id: 'reference', header: 'Referans', kind: 'code', cell: (t) => <span translate="no">{t.reference}</span> },
    { id: 'date', header: 'Tarih', kind: 'date', cell: (t) => formatDateTime(t.createdAt) },
    { id: 'by', header: 'Kaydeden', cell: (t) => personLabel(t.createdByDisplayName, t.createdBy, me.id) },
    { id: 'from', header: 'Kaynak', cell: (t) => depot(t.fromWarehouseId) },
    { id: 'to', header: 'Hedef', cell: (t) => depot(t.toWarehouseId) },
    {
      id: 'lines',
      header: 'Satırlar',
      kind: 'name',
      cell: (t) => (
        <ul>
          {t.lines.map((l) => (
            <li key={l.lineNo}>
              <Code>{l.sku}</Code> × <span className="num">{formatQuantity(l.quantity)}</span> {l.unit}
            </li>
          ))}
        </ul>
      ),
    },
    { id: 'total', header: 'Toplam', kind: 'num', cell: (t) => formatQuantity(t.lines.reduce((acc, l) => acc + l.quantity, 0)) },
  ];

  return (
    <div className="view">
      <PageHead title="Transfer" sub={<WarehouseTag warehouse={selected} />} lead="Depolar arası tek adımlı iç aktarım. Kaynak düşüşü ve hedef artışı birlikte kaydedilir." />
      <LiveRegion>
        {posted !== null && (
          <SuccessNotice title={`Transfer ${posted.number} kaydedildi`}>
            <p>
              <Code>{posted.reference}</Code> referansıyla {formatQuantity(posted.lines.reduce((acc, l) => acc + l.quantity, 0))} adet {nameOf(posted.fromWarehouseId)}{' '}
              deposundan {nameOf(posted.toWarehouseId)} deposuna aktarıldı.
            </p>
          </SuccessNotice>
        )}
      </LiveRegion>
      <TransferForm
        onPosted={async (transfer) => {
          setPosted(transfer);
          await Promise.all([invalidate('transfers'), invalidate('balances'), invalidate('movements'), invalidate('product-picker')]);
        }}
      />
      <ListRegion
        list={list}
        loadingLabel="Transferler yükleniyor…"
        noun="transfer"
        scopeNoun="Depo"
        empty={
          <EmptyState title="Bu depoya ait transfer yok" level={2}>
            Seçili deponun kaynak ya da hedef olduğu transferler burada listelenir.
          </EmptyState>
        }
      >
        {(items) => <DataTable caption="Transferler" captionNote="Seçili deponun kaynak ya da hedef olduğu belgeler" columns={columns} rows={items} rowKey={(t) => t.id} />}
      </ListRegion>
    </div>
  );
}

// ---------------------------------------------------------------- adjustment
const ADJUSTMENT_FORM = 'duzeltme';
const REASONS: readonly AdjustmentReason[] = ['CountDifference', 'Damage', 'Loss', 'Found', 'DataCorrection'];

interface AdjustmentCommand {
  reference: string;
  warehouseId: string;
  reasonCode: AdjustmentReason;
  note: string;
  lines: { productId: string; quantityDelta: number }[];
}

function AdjustmentForm({ onPosted }: { onPosted: (adjustment: Adjustment) => void | Promise<void> }) {
  const me = useMe();
  const { selected } = useWarehouse();
  const supervised = warehousesWith(me, 'Supervise');
  const confirmStep = useDisclosure('duzeltme-onay');
  const [reference, setReference] = useState('');
  const [warehouseId, setWarehouseId] = useState(supervised.some((w) => w.id === selected?.id) ? (selected?.id ?? '') : (supervised[0]?.id ?? ''));
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const [lines, setLines] = useState<StockLine[]>([]);
  const [local, setLocal] = useState<Record<string, string>>({});
  const [sent, setSent] = useState<string[]>([]);
  const [attempt, setAttempt] = useState(0);
  const [ready, setReady] = useState<AdjustmentCommand | null>(null);
  // Found stock of a product that never moved in this warehouse has no balance row to pick from.
  const [catalog, setCatalog] = useState(false);

  const post = useCommand((command: AdjustmentCommand, key: string) => inventoryApi.createAdjustment(command, key), {
    onSuccess: async (adjustment) => {
      confirmStep.hide();
      setReady(null);
      setReference('');
      setReason('');
      setNote('');
      setLines([]);
      setLocal({});
      await onPosted(adjustment);
    },
    onProblem: (_problem, command) => {
      setSent(command.lines.map((l) => l.productId));
      setAttempt((n) => n + 1);
    },
  });

  const server = lineErrors(post.problem, sent);
  const errorOf = (key: string): string | undefined => local[key] ?? server.byField[key];
  const lineError = (productId: string): string | undefined => local[`line:${productId}`] ?? server.byProduct[productId];

  const invalidateReady = () => {
    if (ready !== null) {
      setReady(null);
      confirmStep.hide();
    }
  };

  /** First step: validate and show exactly what will be posted. Nothing is sent yet. */
  const review = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const ref = normalizeReference(reference);
    if (!REFERENCE_PATTERN.test(ref)) {
      next.reference = '3–48 karakter; yalnız A–Z, 0–9 ve - _ / . kullanılabilir.';
    }

    if (warehouseId.length === 0) {
      next.warehouseId = 'Depoyu seçin.';
    }

    const reasonCode = REASONS.find((r) => r === reason);
    if (reasonCode === undefined) {
      next.reasonCode = 'Gerekçe kodunu seçin.';
    }

    const trimmed = note.trim();
    if (trimmed.length < 10 || trimmed.length > 500) {
      next.note = 'Açıklama zorunludur ve 10–500 karakter olmalıdır.';
    }

    if (lines.length === 0) {
      next.lines = 'En az bir ürün satırı ekleyin.';
    }

    const payload: AdjustmentCommand['lines'] = [];
    for (const line of lines) {
      const delta = parseSignedQuantity(line.value);
      if (delta === null || delta === 0 || Math.abs(delta) > MAX_QUANTITY) {
        next[`line:${line.productId}`] = `Sıfırdan farklı bir tam sayı girin (örn. +5 veya -3); en fazla ${formatQuantity(MAX_QUANTITY)}.`;
      } else if (delta < 0 && line.onHand + delta < line.reserved) {
        next[`line:${line.productId}`] = `Rezerve stok düzeltmeyle düşülemez: en fazla ${formatQuantity(line.available)} azaltılabilir.`;
      } else {
        payload.push({ productId: line.productId, quantityDelta: delta });
      }
    }

    setLocal(next);
    post.clearProblem();
    if (Object.keys(next).length > 0 || reasonCode === undefined) {
      setReady(null);
      setAttempt((n) => n + 1);
      return;
    }

    setReady({ reference: ref, warehouseId, reasonCode, note: trimmed, lines: payload });
    confirmStep.show();
  };

  const summary: SummaryItem[] = [];
  const add = (key: string, label: string, fieldId: string) => {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId, message: `${label}: ${message}` });
    }
  };
  add('reference', 'Düzeltme referansı', fieldDomId(ADJUSTMENT_FORM, 'reference'));
  add('warehouseId', 'Depo', fieldDomId(ADJUSTMENT_FORM, 'warehouseId'));
  add('reasonCode', 'Gerekçe', fieldDomId(ADJUSTMENT_FORM, 'reasonCode'));
  add('note', 'Açıklama', fieldDomId(ADJUSTMENT_FORM, 'note'));
  add('lines', 'Satırlar', fieldDomId(ADJUSTMENT_FORM, 'urun-ara'));
  for (const line of lines) {
    const message = lineError(line.productId);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(ADJUSTMENT_FORM, `satir-${line.productId}`), message: `${line.sku} ${line.name}: ${message}` });
    }
  }

  for (const message of server.rest) {
    summary.push({ message });
  }

  if (supervised.length === 0) {
    return (
      <InfoNote tone="warn" title="Stok düzeltmesi için depo sorumlusu yetkisi gerekir">
        <p>Düzeltme, depoda “Sorumlu” seviyesinde yetkisi olan depo görevlisi tarafından kaydedilir. Hiçbir depoda bu yetkiniz yok.</p>
      </InfoNote>
    );
  }

  const errors: Record<string, string | undefined> = {};
  for (const line of lines) {
    errors[line.productId] = lineError(line.productId);
  }

  const problem = post.problem;
  return (
    <div className="stack">
      <form className="region" onSubmit={review} noValidate aria-labelledby="duzeltme-baslik">
        <div className="region--pad stack">
          <h2 className="section-h section-h--flush" id="duzeltme-baslik">
            Yeni stok düzeltmesi
          </h2>
          {summary.length > 0 && (
            <FormErrorSummary
              title={`Düzeltme kaydedilmedi: ${summary.length} alanı düzeltin`}
              items={summary}
              focusKey={attempt}
              note={problem !== null ? `${problem.title}. Hiçbir satır kaydedilmedi; stok değişmedi.` : 'Hiçbir satır kaydedilmedi; stok değişmedi.'}
            />
          )}
          {problem !== null && summary.length === 0 && (
            <ProblemAlert
              problem={problem}
              title="Düzeltme kaydedilmedi"
              onRetry={problem.isRetryable && ready !== null ? () => void post.run(ready) : undefined}
            >
              {!problem.isRetryable && <p>Hiçbir satır kaydedilmedi; stok değişmedi.</p>}
            </ProblemAlert>
          )}

          <div className="formgrid">
            <TextField
              formId={ADJUSTMENT_FORM}
              name="reference"
              label="Düzeltme referansı"
              value={reference}
              onChange={(value) => {
                invalidateReady();
                setReference(value);
              }}
              inputClassName="code"
              spellCheck={false}
              autoCapitalize="characters"
              maxLength={48}
              required
              translate="no"
              error={errorOf('reference')}
              hint="Sayım tutanağı ya da kayıt numarası. Aynı referans ikinci kez kaydedilemez."
            />
            <SelectField
              formId={ADJUSTMENT_FORM}
              name="warehouseId"
              label="Depo"
              value={warehouseId}
              onChange={(value) => {
                invalidateReady();
                setWarehouseId(value);
                setLines([]);
              }}
              options={supervised.map((w) => ({ value: w.id, label: `${w.code} · ${w.name}` }))}
              required
              error={errorOf('warehouseId')}
              hint={lines.length > 0 ? 'Depoyu değiştirirseniz satırlar temizlenir.' : undefined}
            />
            <SelectField
              formId={ADJUSTMENT_FORM}
              name="reasonCode"
              label="Gerekçe kodu"
              value={reason}
              onChange={(value) => {
                invalidateReady();
                setReason(value);
              }}
              options={REASONS.map((r) => ({ value: r, label: ADJUSTMENT_REASON_LABELS[r] }))}
              placeholder="Gerekçe seçin"
              required
              error={errorOf('reasonCode')}
            />
            <TextAreaField
              formId={ADJUSTMENT_FORM}
              name="note"
              label="Açıklama (zorunlu)"
              value={note}
              onChange={(value) => {
                invalidateReady();
                setNote(value);
              }}
              rows={3}
              maxLength={500}
              counter
              required
              fieldClassName="field--full"
              error={errorOf('note')}
              hint="Ne sayıldı, ne bulundu, kim tespit etti: 10–500 karakter. Denetim kaydında görünür."
            />
          </div>

          <CheckField
            formId={ADJUSTMENT_FORM}
            name="catalog"
            label="Bu depoda bakiyesi olmayan bir ürün arıyorum (ürün kataloğunda ara)"
            checked={catalog}
            onChange={setCatalog}
          />
          {warehouseId.length > 0 && (
            <ProductPicker
              key={catalog ? 'catalog' : 'stock'}
              formId={ADJUSTMENT_FORM}
              source={catalog ? { kind: 'catalog' } : { kind: 'stock', warehouseId }}
              excluded={new Set(lines.map((l) => l.productId))}
              onPick={(product) => {
                if (lines.length < MAX_LINES) {
                  invalidateReady();
                  setLines((previous) => [...previous, toStockLine(product)]);
                }
              }}
            />
          )}
        </div>

        {lines.length === 0 ? (
          <p className="region--pad small">
            Henüz satır yok. Depodaki bakiyelerden ürün arayıp ekleyin. Bu depoda hiç hareket görmemiş bir ürün bakiye listesinde yer almaz; onun
            için katalog aramasını işaretleyin (eldeki miktarı 0 kabul edilir).
          </p>
        ) : (
          <StockLines
            formId={ADJUSTMENT_FORM}
            mode="adjustment"
            lines={lines}
            errors={errors}
            onChange={(productId, value) => {
              invalidateReady();
              setLines((previous) => previous.map((l) => (l.productId === productId ? { ...l, value } : l)));
            }}
            onRemove={(productId) => {
              invalidateReady();
              setLines((previous) => previous.filter((l) => l.productId !== productId));
            }}
          />
        )}

        <p className="region--pad small actions--ruled">Artı değer stoğu artırır, eksi değer azaltır. Rezerve miktarın altına inilemez.</p>
        <div className="region--pad actions actions--sticky actions--end">
          <Button
            type="submit"
            variant="primary"
            aria-expanded={confirmStep.open}
            aria-controls={confirmStep.panelId}
          >
            Düzeltmeyi gözden geçir…
          </Button>
        </div>
      </form>

      {ready !== null && (
        <ConfirmStep
          disclosure={confirmStep}
          confirmLabel="Düzeltmeyi kaydet"
          pendingLabel="Düzeltme kaydediliyor…"
          pending={post.pending}
          onConfirm={() => void post.run(ready)}
          question={
            <>
              <strong>
                <Code>{ready.reference}</Code> referanslı {ready.lines.length} satırlık düzeltme kaydedilsin mi?
              </strong>{' '}
              Gerekçe: {ADJUSTMENT_REASON_LABELS[ready.reasonCode]}. Kayıt stok miktarını hemen değiştirir ve sonradan düzenlenemez; hata olursa
              ters yönde yeni bir düzeltme gerekir.
            </>
          }
        >
          <ul className="small">
            {ready.lines.map((l) => {
              const line = lines.find((x) => x.productId === l.productId);
              return (
                <li key={l.productId}>
                  <Code>{line?.sku ?? l.productId}</Code> {line?.name}: <span className="num">{formatSigned(l.quantityDelta)}</span>
                  {line !== undefined && <>; kayıttan sonra eldeki {formatQuantity(line.onHand + l.quantityDelta)}</>}
                </li>
              );
            })}
          </ul>
        </ConfirmStep>
      )}
    </div>
  );
}

export function AdjustmentScreen() {
  const me = useMe();
  const { selected, nameOf } = useWarehouse();
  const invalidate = useInvalidate();
  const [posted, setPosted] = useState<Adjustment | null>(null);
  const list = useCursorList<Adjustment>(
    ['adjustments', selected?.id ?? null],
    (cursor, signal) => inventoryApi.adjustments({ warehouseId: selected?.id ?? '', cursor }, signal),
    { enabled: selected !== null },
  );

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title="Stok düzeltmesi" />
        <NoWarehouse />
      </div>
    );
  }

  const columns: Column<Adjustment>[] = [
    { id: 'number', header: 'Düzeltme no', kind: 'code', rowHeader: true, cell: (a) => <span translate="no">{a.number}</span> },
    { id: 'reference', header: 'Referans', kind: 'code', cell: (a) => <span translate="no">{a.reference}</span> },
    { id: 'date', header: 'Tarih', kind: 'date', cell: (a) => formatDateTime(a.createdAt) },
    { id: 'by', header: 'Kaydeden', cell: (a) => personLabel(a.createdByDisplayName, a.createdBy, me.id) },
    { id: 'reason', header: 'Gerekçe', cell: (a) => labelOf(ADJUSTMENT_REASON_LABELS, a.reasonCode) },
    { id: 'note', header: 'Açıklama', kind: 'name', cell: (a) => a.note },
    {
      id: 'lines',
      header: 'Satırlar',
      kind: 'name',
      cell: (a) => (
        <ul>
          {a.lines.map((l) => (
            <li key={l.lineNo}>
              <Code>{l.sku}</Code> <span className="num">{formatSigned(l.quantityDelta)}</span> {l.unit}
            </li>
          ))}
        </ul>
      ),
    },
  ];

  return (
    <div className="view">
      <PageHead
        title="Stok düzeltmesi"
        sub={<WarehouseTag warehouse={selected} />}
        lead="Sayım farkı, hasar, kayıp ve bulunan stok için gerekçeli artı/eksi kayıt. Her düzeltme hareket günlüğüne yazılır ve değiştirilemez."
      />
      <LiveRegion>
        {posted !== null && (
          <SuccessNotice title={`Düzeltme ${posted.number} kaydedildi`}>
            <p>
              <Code>{posted.reference}</Code> referansıyla {nameOf(posted.warehouseId)} deposunda {posted.lines.length} satır düzeltildi (
              {labelOf(ADJUSTMENT_REASON_LABELS, posted.reasonCode)}).
            </p>
          </SuccessNotice>
        )}
      </LiveRegion>
      <AdjustmentForm
        onPosted={async (adjustment) => {
          setPosted(adjustment);
          await Promise.all([invalidate('adjustments'), invalidate('balances'), invalidate('movements'), invalidate('product-picker')]);
        }}
      />
      <ListRegion
        list={list}
        loadingLabel="Düzeltmeler yükleniyor…"
        noun="düzeltme"
        scopeNoun="Depo"
        empty={<EmptyState title="Bu depoda henüz stok düzeltmesi yok">Kaydedilen düzeltmeler gerekçesi ve açıklamasıyla burada listelenir.</EmptyState>}
      >
        {(items) => <DataTable caption="Düzeltmeler" columns={columns} rows={items} rowKey={(a) => a.id} />}
      </ListRegion>
    </div>
  );
}
