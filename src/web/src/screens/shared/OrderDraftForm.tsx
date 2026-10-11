import { useQueries } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { inventoryApi, masterApi, purchasingApi, salesApi } from '../../api/endpoints';
import { asProblem } from '../../api/problem';
import type { PurchaseOrder, SalesOrder } from '../../api/types';
import { hasRole } from '../../auth/permissions';
import { useMe, useUserKey } from '../../auth/session';
import { FormErrorSummary, SelectField, TextAreaField, fieldDomId } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { Icon } from '../../components/Icon';
import { ProductPicker } from '../../components/ProductPicker';
import type { PickedProduct } from '../../components/ProductPicker';
import { InfoNote, ProblemAlert, StaleAlert } from '../../components/states';
import { Button, Code } from '../../components/ui';
import { formatDecimal, formatMoney, formatQuantity, parseMoney, parseQuantity } from '../../lib/format';
import { useApiQuery } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { useWarehouse } from '../../shell/context';

export interface DraftLine {
  productId: string;
  sku: string;
  name: string;
  unit: string;
  quantity: string;
  /** Purchase orders: cost typed by the buyer. */
  unitCost: string;
  /** Sales orders: the server's price, shown read-only. The client never sends a price. */
  price?: number | undefined;
}

export interface DraftInitial {
  id: string;
  number: string;
  partyId: string;
  partyLabel: string;
  warehouseId: string;
  note: string;
  lines: DraftLine[];
}

interface Payload {
  partyId: string;
  warehouseId: string;
  note: string | undefined;
  lines: { productId: string; quantity: number; unitCost: number }[];
}

interface OrderDraftFormProps {
  kind: 'sales' | 'purchase';
  /** Existing draft to edit; omit to create a new one. */
  initial?: DraftInitial | undefined;
  /** ETag of the draft as loaded (edit only); sent as If-Match. */
  etag?: string | undefined;
  /** Re-reads the draft after a 412 so the next save carries the current ETag. The form keeps its values. */
  onReload?: (() => Promise<unknown>) | undefined;
  onSaved: (order: SalesOrder | PurchaseOrder) => void;
  onCancel: () => void;
}

const MAX_LINES = 50;
const MAX_QUANTITY = 100_000;

const TEXT = {
  sales: {
    formId: 'siparis',
    party: 'Müşteri',
    partyField: 'customerId',
    partyPlaceholder: 'Müşteri seçin',
    partyLoading: 'Müşteriler yükleniyor…',
    partyEmpty: 'Aktif müşteri yok. Müşteriyi sistem yöneticisi tanımlar.',
    warehouse: 'Sevk deposu',
    save: 'Taslağı kaydet',
    create: 'Taslak siparişi oluştur',
    failed: 'Sipariş kaydedilmedi',
  },
  purchase: {
    formId: 'satinalma',
    party: 'Tedarikçi',
    partyField: 'supplierId',
    partyPlaceholder: 'Tedarikçi seçin',
    partyLoading: 'Tedarikçiler yükleniyor…',
    partyEmpty: 'Aktif tedarikçi yok. Tedarikçiyi sistem yöneticisi tanımlar.',
    warehouse: 'Teslim deposu',
    save: 'Taslağı kaydet',
    create: 'Taslak siparişi oluştur',
    failed: 'Satın alma siparişi kaydedilmedi',
  },
} as const;

/**
 * Draft editor shared by sales and purchase orders: party, warehouse, note and lines. Prices of a
 * sales order are the server's; the form shows them read-only and sends only product and quantity.
 */
export function OrderDraftForm({ kind, initial, etag, onReload, onSaved, onCancel }: OrderDraftFormProps) {
  const t = TEXT[kind];
  const me = useMe();
  const userKey = useUserKey();
  const { warehouses, selected } = useWarehouse();
  const editing = initial !== undefined;
  const [partyId, setPartyId] = useState(initial?.partyId ?? '');
  const [warehouseId, setWarehouseId] = useState(initial?.warehouseId ?? selected?.id ?? '');
  const [note, setNote] = useState(initial?.note ?? '');
  const [lines, setLines] = useState<DraftLine[]>(initial?.lines ?? []);
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);
  const [dirty, setDirty] = useState(false);
  const [reloaded, setReloaded] = useState(false);
  const [reloading, setReloading] = useState(false);

  const parties = useApiQuery([kind === 'sales' ? 'customers' : 'suppliers', 'active'], async (signal) => {
    const list = kind === 'sales' ? await masterApi.customers(true, signal) : await masterApi.suppliers(true, signal);
    return list.map((p) => ({ value: p.id, label: `${p.code} · ${p.name}` }));
  });

  // Availability hint for sales lines: what the selected warehouse can promise right now.
  const stockReadable = kind === 'sales' && hasRole(me, 'SalesAgent', 'SalesManager') && warehouseId.length > 0;
  const availability = useQueries({
    queries: lines.map((line) => ({
      queryKey: userKey('line-availability', warehouseId, line.sku),
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const page = await inventoryApi.balances({ warehouseId, search: line.sku, limit: 10 }, signal);
        return page.items.find((b) => b.productId === line.productId)?.available ?? 0;
      },
      enabled: stockReadable,
      staleTime: 15_000,
    })),
  });

  const save = useCommand(
    (payload: Payload, key: string): Promise<SalesOrder | PurchaseOrder> => {
      if (kind === 'sales') {
        const body = {
          customerId: payload.partyId,
          warehouseId: payload.warehouseId,
          note: payload.note,
          lines: payload.lines.map((l) => ({ productId: l.productId, quantity: l.quantity })),
        };
        return initial !== undefined ? salesApi.update(initial.id, body, etag ?? '') : salesApi.create(body, key);
      }

      const body = { supplierId: payload.partyId, warehouseId: payload.warehouseId, note: payload.note, lines: payload.lines };
      return initial !== undefined ? purchasingApi.update(initial.id, body, etag ?? '') : purchasingApi.create(body, key);
    },
    {
      onSuccess: (order) => {
        setDirty(false);
        onSaved(order);
      },
      onProblem: () => setAttempt((n) => n + 1),
    },
  );

  // Leaving the page with unsaved input asks the browser to confirm.
  useEffect(() => {
    if (!dirty) {
      return undefined;
    }

    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const touch = () => {
    setDirty(true);
    setReloaded(false);
  };

  const addLine = (product: PickedProduct) => {
    if (lines.length >= MAX_LINES || lines.some((l) => l.productId === product.productId)) {
      return;
    }

    touch();
    setLines((previous) => [
      ...previous,
      { productId: product.productId, sku: product.sku, name: product.name, unit: product.unit, quantity: '', unitCost: '', price: product.listPrice },
    ]);
  };

  const patchLine = (productId: string, patch: Partial<DraftLine>) => {
    touch();
    setLines((previous) => previous.map((line) => (line.productId === productId ? { ...line, ...patch } : line)));
  };

  const removeLine = (productId: string) => {
    touch();
    setLines((previous) => previous.filter((line) => line.productId !== productId));
  };

  const partyFieldId = fieldDomId(t.formId, t.partyField);
  const warehouseFieldId = fieldDomId(t.formId, 'warehouseId');
  const quantityId = (productId: string) => fieldDomId(t.formId, `miktar-${productId}`);
  const costId = (productId: string) => fieldDomId(t.formId, `maliyet-${productId}`);

  // Server field paths ("lines[2].quantity") mapped onto the same keys the local validation uses.
  const server: Record<string, string> = {};
  const general: string[] = [];
  if (save.problem !== null && !save.problem.isStale) {
    for (const error of save.problem.errors) {
      const match = /^lines\[(\d+)\](?:\.(\w+))?$/.exec(error.field);
      const line = match ? lines[Number(match[1])] : undefined;
      if (line !== undefined) {
        server[`${match?.[2] === 'unitCost' ? 'cost' : 'qty'}:${line.productId}`] = error.detail;
      } else if (error.field === t.partyField || error.field === 'warehouseId' || error.field === 'note' || error.field === 'lines') {
        server[error.field] = error.detail;
      } else {
        general.push(error.detail.length > 0 ? error.detail : error.code);
      }
    }
  }

  const errorOf = (key: string): string | undefined => local[key] ?? server[key];

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (partyId.length === 0) {
      next[t.partyField] = `${t.party} seçin.`;
    }

    if (warehouseId.length === 0) {
      next.warehouseId = 'Depo seçin.';
    }

    if (note.trim().length > 500) {
      next.note = 'Not en fazla 500 karakter olabilir.';
    }

    if (lines.length === 0) {
      next.lines = 'En az bir ürün satırı ekleyin.';
    }

    const payloadLines: Payload['lines'] = [];
    for (const line of lines) {
      const quantity = parseQuantity(line.quantity);
      if (quantity === null || quantity < 1 || quantity > MAX_QUANTITY) {
        next[`qty:${line.productId}`] = `Miktar 1 ile ${formatQuantity(MAX_QUANTITY)} arasında bir tam sayı olmalıdır.`;
      }

      let unitCost = 0;
      if (kind === 'purchase') {
        const parsed = parseMoney(line.unitCost);
        if (parsed === null || parsed > 99_999_999.99) {
          next[`cost:${line.productId}`] = 'Birim maliyet 0 veya daha büyük, en fazla 2 ondalıklı bir tutar olmalıdır (örn. 142,50).';
        } else {
          unitCost = parsed;
        }
      }

      if (quantity !== null) {
        payloadLines.push({ productId: line.productId, quantity, unitCost });
      }
    }

    setLocal(next);
    save.clearProblem();
    if (Object.keys(next).length > 0) {
      setAttempt((n) => n + 1);
      return;
    }

    const trimmedNote = note.trim();
    void save.run({ partyId, warehouseId, note: trimmedNote.length > 0 ? trimmedNote : undefined, lines: payloadLines });
  };

  const reload = async () => {
    if (onReload === undefined) {
      return;
    }

    setReloading(true);
    try {
      await onReload();
      save.clearProblem();
      setReloaded(true);
    } finally {
      setReloading(false);
    }
  };

  const summary: SummaryItem[] = [];
  const push = (key: string, fieldId: string | undefined, label: string) => {
    const message = errorOf(key);
    if (message !== undefined) {
      summary.push({ fieldId, message: `${label}: ${message}` });
    }
  };
  push(t.partyField, partyFieldId, t.party);
  push('warehouseId', warehouseFieldId, t.warehouse);
  push('note', fieldDomId(t.formId, 'note'), 'Not');
  push('lines', fieldDomId(t.formId, 'urun-ara'), 'Satırlar');
  for (const line of lines) {
    push(`qty:${line.productId}`, quantityId(line.productId), `${line.sku} miktar`);
    push(`cost:${line.productId}`, costId(line.productId), `${line.sku} birim maliyet`);
  }

  for (const message of general) {
    summary.push({ message });
  }

  const partyOptions = [...(parties.data ?? [])];
  if (initial !== undefined && !partyOptions.some((option) => option.value === initial.partyId)) {
    partyOptions.unshift({ value: initial.partyId, label: `${initial.partyLabel} (pasif olabilir)` });
  }

  const warehouseOptions = warehouses.map((w) => ({ value: w.id, label: `${w.code} · ${w.name}` }));
  const excluded = new Set(lines.map((line) => line.productId));
  let estimate = 0;
  let estimateComplete = lines.length > 0;
  for (const line of lines) {
    const quantity = parseQuantity(line.quantity);
    const unit = kind === 'sales' ? (line.price ?? null) : parseMoney(line.unitCost);
    if (quantity === null || unit === null) {
      estimateComplete = false;
    } else {
      estimate += quantity * unit;
    }
  }

  const problem = save.problem;
  return (
    <form className="stack stack--wide" onSubmit={submit} noValidate aria-label={editing ? 'Taslağı düzenle' : 'Yeni sipariş'}>
      {problem !== null && problem.isStale && <StaleAlert what="taslak" onReload={() => void reload()} pending={reloading} problem={problem} />}
      {reloaded && (
        <InfoNote title="Güncel sürüm yüklendi">
          <p>
            Aşağıdaki değerler sizin girdiklerinizdir. Kaydederseniz taslağın güncel içeriğinin yerine geçer; önce güncel içeriği görmek
            isterseniz vazgeçip belgeyi açın.
          </p>
        </InfoNote>
      )}
      {summary.length > 0 && (
        <FormErrorSummary
          title={`${t.failed}: ${summary.length} alanı düzeltin`}
          items={summary}
          focusKey={attempt}
          note={problem !== null ? `${problem.title}. Hiçbir değişiklik kaydedilmedi.` : 'Hiçbir değişiklik kaydedilmedi.'}
        />
      )}
      {problem !== null && !problem.isStale && summary.length === 0 && (
        <ProblemAlert
          problem={problem}
          title={t.failed}
          onRetry={problem.isRetryable ? () => document.getElementById(`${t.formId}-kaydet`)?.click() : undefined}
        />
      )}

      <section className="region region--pad stack" aria-labelledby={`${t.formId}-baslik`}>
        <h2 className="section-h section-h--flush" id={`${t.formId}-baslik`}>
          Belge bilgileri
        </h2>
        <div className="formgrid">
          {parties.isPending ? (
            <p role="status" aria-busy="true" className="small">
              {t.partyLoading}
            </p>
          ) : parties.isError ? (
            <ProblemAlert problem={asProblem(parties.error)} title={`${t.party} listesi yüklenemedi`} onRetry={() => void parties.refetch()} headingLevel={3} />
          ) : (
            <SelectField
              formId={t.formId}
              name={t.partyField}
              label={t.party}
              value={partyId}
              onChange={(value) => {
                touch();
                setPartyId(value);
              }}
              options={partyOptions}
              placeholder={t.partyPlaceholder}
              required
              error={errorOf(t.partyField)}
              hint={partyOptions.length === 0 ? t.partyEmpty : undefined}
            />
          )}
          {editing ? (
            <div className="field">
              <span className="legend">{t.warehouse}</span>
              <span>
                {warehouseOptions.find((option) => option.value === warehouseId)?.label ?? 'Yetkiniz olmayan depo'}
              </span>
              <span className="hint">Siparişin deposu oluşturulduktan sonra değiştirilemez.</span>
            </div>
          ) : (
            <SelectField
              formId={t.formId}
              name="warehouseId"
              label={t.warehouse}
              value={warehouseId}
              onChange={(value) => {
                touch();
                setWarehouseId(value);
              }}
              options={warehouseOptions}
              placeholder="Depo seçin"
              required
              error={errorOf('warehouseId')}
              hint={warehouseOptions.length === 0 ? 'Hiçbir depo için yetkiniz yok; sistem yöneticinize başvurun.' : undefined}
            />
          )}
          <TextAreaField
            formId={t.formId}
            name="note"
            label="Not (isteğe bağlı)"
            value={note}
            onChange={(value) => {
              touch();
              setNote(value);
            }}
            rows={2}
            maxLength={500}
            counter
            fieldClassName="field--full"
            error={errorOf('note')}
          />
        </div>
      </section>

      <section className="region" aria-labelledby={`${t.formId}-satirlar`}>
        <div className="region--pad stack">
          <h2 className="section-h section-h--flush" id={`${t.formId}-satirlar`}>
            Satırlar
          </h2>
          <ProductPicker formId={t.formId} source={{ kind: 'catalog' }} excluded={excluded} onPick={addLine} />
          {lines.length >= MAX_LINES && <p className="small">Bir belgede en fazla {MAX_LINES} satır olabilir.</p>}
        </div>

        {lines.length === 0 ? (
          <p className="region--pad small">Henüz satır yok. Yukarıdan ürün arayıp ekleyin; aynı ürün bir belgede yalnız bir satırda yer alır.</p>
        ) : (
          <table className="stacked" role="table">
            <caption className="sr-only">Sipariş satırları</caption>
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
                  Miktar
                </th>
                {kind === 'sales' && stockReadable && (
                  <th scope="col" role="columnheader" className="num">
                    Kullanılabilir
                  </th>
                )}
                <th scope="col" role="columnheader" className="num">
                  {kind === 'sales' ? 'Liste fiyatı' : 'Birim maliyet'}
                </th>
                <th scope="col" role="columnheader" className="num">
                  Tutar
                </th>
                <th scope="col" role="columnheader">
                  <span className="sr-only">İşlem</span>
                </th>
              </tr>
            </thead>
            <tbody role="rowgroup">
              {lines.map((line, index) => {
                const quantity = parseQuantity(line.quantity);
                const unit = kind === 'sales' ? (line.price ?? null) : parseMoney(line.unitCost);
                const qtyError = errorOf(`qty:${line.productId}`);
                const costError = errorOf(`cost:${line.productId}`);
                const stock = availability[index];
                const available = stock?.data;
                const short = available !== undefined && quantity !== null && quantity > available;
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
                    <td role="cell" className="num" data-label="Miktar">
                      <span className="cell-stack">
                        <label className="sr-only" htmlFor={quantityId(line.productId)}>
                          {line.sku} miktarı
                        </label>
                        <input
                          id={quantityId(line.productId)}
                          name={`quantity-${line.sku}`}
                          className="input input--qty"
                          type="text"
                          inputMode="numeric"
                          pattern="[0-9]*"
                          autoComplete="off"
                          value={line.quantity}
                          onChange={(event) => patchLine(line.productId, { quantity: event.target.value })}
                          aria-invalid={qtyError !== undefined || undefined}
                          aria-describedby={qtyError !== undefined ? `${quantityId(line.productId)}-hata` : undefined}
                        />
                        {qtyError !== undefined && (
                          <span className="field-error" id={`${quantityId(line.productId)}-hata`}>
                            <Icon name="error" />
                            {qtyError}
                          </span>
                        )}
                      </span>
                    </td>
                    {kind === 'sales' && stockReadable && (
                      <td role="cell" className="num" data-label="Kullanılabilir">
                        {stock === undefined || stock.isPending ? (
                          <span className="muted">…</span>
                        ) : stock.isError ? (
                          <span className="muted">okunamadı</span>
                        ) : (
                          <span className="cell-stack">
                            <span>{formatQuantity(available ?? 0)}</span>
                            {short && (
                              <span className="field-error">
                                <Icon name="warn" />
                                Onayda stok yetmeyebilir
                              </span>
                            )}
                          </span>
                        )}
                      </td>
                    )}
                    <td role="cell" className="num" data-label={kind === 'sales' ? 'Liste fiyatı' : 'Birim maliyet'}>
                      {kind === 'sales' ? (
                        line.price !== undefined ? (
                          formatMoney(line.price)
                        ) : (
                          <span className="muted">kayıtta belirlenir</span>
                        )
                      ) : (
                        <span className="cell-stack">
                          <label className="sr-only" htmlFor={costId(line.productId)}>
                            {line.sku} birim maliyeti (TRY)
                          </label>
                          <input
                            id={costId(line.productId)}
                            name={`unitCost-${line.sku}`}
                            className="input input--money"
                            type="text"
                            inputMode="decimal"
                            autoComplete="off"
                            placeholder="0,00"
                            value={line.unitCost}
                            onChange={(event) => patchLine(line.productId, { unitCost: event.target.value })}
                            aria-invalid={costError !== undefined || undefined}
                            aria-describedby={costError !== undefined ? `${costId(line.productId)}-hata` : undefined}
                          />
                          {costError !== undefined && (
                            <span className="field-error" id={`${costId(line.productId)}-hata`}>
                              <Icon name="error" />
                              {costError}
                            </span>
                          )}
                        </span>
                      )}
                    </td>
                    <td role="cell" className="num" data-label="Tutar">
                      {quantity !== null && unit !== null ? formatMoney(quantity * unit) : <span className="muted">—</span>}
                    </td>
                    <td role="cell" data-label="İşlem">
                      <button
                        type="button"
                        className="btn btn--small"
                        aria-label={`${line.sku} satırını kaldır`}
                        onClick={() => removeLine(line.productId)}
                      >
                        <Icon name="trash" />
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
            <tfoot role="rowgroup">
              <tr role="row">
                <th scope="row" role="rowheader" colSpan={kind === 'sales' && stockReadable ? 6 : 5}>
                  Tahmini toplam ({lines.length} satır)
                </th>
                <td role="cell" className="num" data-label="Tahmini toplam">
                  {estimateComplete ? formatMoney(estimate) : <span className="muted">—</span>}
                </td>
                <td role="cell" />
              </tr>
            </tfoot>
          </table>
        )}
        <p className="region--pad small">
          {kind === 'sales' ? (
            <>
              Fiyatlar sunucudaki liste fiyatıdır ve sipariş kaydedilirken sunucu tarafından yazılır; buradan değiştirilemez. Stok, sipariş{' '}
              <strong>onaylandığında</strong> rezerve edilir — taslak stok ayırmaz.
            </>
          ) : (
            <>
              Birim maliyeti siz girersiniz (örn. <Code>{formatDecimal(142.5)}</Code>). Stok, mal kabul kaydedildiğinde artar — sipariş stok
              oluşturmaz.
            </>
          )}
        </p>
      </section>

      <div className="actions actions--sticky">
        <Button onClick={onCancel}>Vazgeç</Button>
        <span className="spacer" />
        <Button id={`${t.formId}-kaydet`} type="submit" variant="primary" pending={save.pending} pendingLabel="Kaydediliyor…">
          {editing ? t.save : t.create}
        </Button>
      </div>
    </form>
  );
}
