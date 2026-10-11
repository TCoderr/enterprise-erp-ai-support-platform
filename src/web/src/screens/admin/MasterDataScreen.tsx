import type { UseQueryResult } from '@tanstack/react-query';
import { useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { masterApi } from '../../api/endpoints';
import type { CodeNameInput, CustomerInput, ProductInput } from '../../api/endpoints';
import { asProblem } from '../../api/problem';
import type { Customer, Product, Supplier, Warehouse } from '../../api/types';
import { PRODUCT_UNITS } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, ConfirmStep, FormErrorSummary, LiveRegion, SelectField, TextAreaField, TextField, fieldDomId, fieldErrorsOf, useDisclosure } from '../../components/forms';
import type { SummaryItem } from '../../components/forms';
import { EmptyState, ProblemAlert, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, PageHead, Qty, Tag } from '../../components/ui';
import { formatDecimal, formatMoney, parseMoney, parseQuantity } from '../../lib/format';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import type { CursorList } from '../../lib/lists';
import { useApiQuery, useInvalidate } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { KLink } from '../../shell/context';

type Values = Record<string, string>;

interface FieldDef {
  name: string;
  label: string;
  kind: 'text' | 'code' | 'money' | 'int' | 'select' | 'textarea';
  options?: readonly string[];
  maxLength?: number;
  hint?: string;
  full?: boolean;
}

interface Entity {
  id: string;
  isActive: boolean;
  eTag: string;
}

interface Config<T extends Entity, I> {
  formId: string;
  /** Singular noun: "ürün", "müşteri". */
  noun: string;
  caption: string;
  columns: Column<T>[];
  fields: FieldDef[];
  empty: Values;
  toValues: (item: T) => Values;
  parse: (values: Values) => { input?: I; errors: Record<string, string> };
  identify: (item: T) => ReactNode;
  create: (input: I, key: string) => Promise<T>;
  update: (id: string, input: I, etag: string) => Promise<T>;
  /** Fields the server treats as immutable once the record exists; shown read-only when editing. */
  lockedOnEdit?: readonly string[];
  setActive: (id: string, active: boolean) => Promise<T>;
  deactivateEffect: string;
  emptyText: string;
}

const CODE = (min: number, max: number) => new RegExp(`^[A-Z0-9\\-_/.]{${min},${max}}$`);
const upper = (text: string) => text.trim().toLocaleUpperCase('en-US');

function requireText(errors: Record<string, string>, values: Values, name: string, min: number, max: number, label: string): string {
  const text = (values[name] ?? '').trim();
  if (text.length < min || text.length > max) {
    errors[name] = `${label} ${min}–${max} karakter olmalıdır.`;
  }

  return text;
}

function requireCode(errors: Record<string, string>, values: Values, name: string, min: number, max: number): string {
  const text = upper(values[name] ?? '');
  if (!CODE(min, max).test(text)) {
    errors[name] = `${min}–${max} karakter; yalnız A–Z, 0–9 ve - _ / . kullanılabilir.`;
  }

  return text;
}

function activeTag(item: Entity) {
  return item.isActive ? (
    <Tag tone="ok" glyph="done">
      Aktif
    </Tag>
  ) : (
    <Tag glyph="cancel">Pasif</Tag>
  );
}

// ---------------------------------------------------------------- configurations
const productConfig: Config<Product, ProductInput> = {
  formId: 'urun',
  noun: 'ürün',
  caption: 'Ürünler',
  columns: [
    { id: 'sku', header: 'SKU', kind: 'code', rowHeader: true, cell: (p) => <span translate="no">{p.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (p) => p.name },
    { id: 'unit', header: 'Birim', cell: (p) => p.unit },
    { id: 'price', header: 'Liste fiyatı', kind: 'num', cell: (p) => (p.listPrice !== undefined ? formatMoney(p.listPrice) : '—') },
    { id: 'reorder', header: 'Sipariş seviyesi', kind: 'num', cell: (p) => <Qty value={p.reorderLevel} /> },
    { id: 'state', header: 'Durum', cell: activeTag },
  ],
  fields: [
    { name: 'sku', label: 'SKU', kind: 'code', maxLength: 32, hint: 'Üründe hareket ya da belge satırı oluştuktan sonra SKU ve birim değiştirilemez.' },
    { name: 'name', label: 'Ürün adı', kind: 'text', maxLength: 160 },
    { name: 'unit', label: 'Birim', kind: 'select', options: PRODUCT_UNITS },
    { name: 'listPrice', label: 'Liste fiyatı (TRY)', kind: 'money', hint: 'Yeni yazılan sipariş satırları bu fiyatı alır; mevcut satırlar değişmez.' },
    { name: 'reorderLevel', label: 'Sipariş seviyesi', kind: 'int', hint: 'Kullanılabilir miktar bu seviyeye indiğinde ürün düşük stokta sayılır.' },
  ],
  empty: { sku: '', name: '', unit: 'ADET', listPrice: '', reorderLevel: '0' },
  toValues: (p) => ({ sku: p.sku, name: p.name, unit: p.unit, listPrice: p.listPrice !== undefined ? formatDecimal(p.listPrice) : '', reorderLevel: String(p.reorderLevel) }),
  parse: (values) => {
    const errors: Record<string, string> = {};
    const sku = requireCode(errors, values, 'sku', 3, 32);
    const name = requireText(errors, values, 'name', 2, 160, 'Ürün adı');
    const unit = values.unit ?? '';
    if (!PRODUCT_UNITS.some((u) => u === unit)) {
      errors.unit = 'Birim seçin.';
    }

    const listPrice = parseMoney(values.listPrice ?? '');
    if (listPrice === null || listPrice > 99_999_999.99) {
      errors.listPrice = 'Tutar 0 veya daha büyük, en fazla 2 ondalıklı olmalıdır (örn. 142,50).';
    }

    const reorderLevel = parseQuantity(values.reorderLevel ?? '');
    if (reorderLevel === null || reorderLevel > 1_000_000) {
      errors.reorderLevel = '0 ile 1.000.000 arasında bir tam sayı girin.';
    }

    return listPrice !== null && reorderLevel !== null && Object.keys(errors).length === 0
      ? { input: { sku, name, unit, listPrice, reorderLevel }, errors }
      : { errors };
  },
  identify: (p) => (
    <>
      <Code>{p.sku}</Code> {p.name}
    </>
  ),
  create: masterApi.createProduct,
  update: masterApi.updateProduct,
  setActive: masterApi.setProductActive,
  deactivateEffect: 'Pasif ürün yeni sipariş satırına eklenemez ve bu ürünü içeren taslaklar onaylanamaz. Onaylı siparişlerin sevkiyatı ve mal kabulü etkilenmez.',
  emptyText: 'Ürün tanımlandığında satış ve satın alma siparişlerinde seçilebilir.',
};

const customerConfig: Config<Customer, CustomerInput> = {
  formId: 'musteri',
  noun: 'müşteri',
  caption: 'Müşteriler',
  columns: [
    { id: 'code', header: 'Kod', kind: 'code', rowHeader: true, cell: (c) => <span translate="no">{c.code}</span> },
    { id: 'name', header: 'Müşteri', kind: 'name', cell: (c) => c.name },
    { id: 'tax', header: 'Vergi no', kind: 'code', cell: (c) => <span translate="no">{c.taxNumber}</span> },
    { id: 'address', header: 'Adres', kind: 'name', cell: (c) => c.address },
    { id: 'state', header: 'Durum', cell: activeTag },
  ],
  fields: [
    { name: 'code', label: 'Müşteri kodu', kind: 'code', maxLength: 32 },
    { name: 'name', label: 'Müşteri adı', kind: 'text', maxLength: 160 },
    { name: 'taxNumber', label: 'Vergi numarası', kind: 'text', maxLength: 11, hint: '10 veya 11 rakam. Bu üründeki tüm veriler sentetiktir.' },
    { name: 'address', label: 'Sevk adresi', kind: 'textarea', maxLength: 400, full: true },
  ],
  empty: { code: '', name: '', taxNumber: '', address: '' },
  toValues: (c) => ({ code: c.code, name: c.name, taxNumber: c.taxNumber, address: c.address }),
  parse: (values) => {
    const errors: Record<string, string> = {};
    const code = requireCode(errors, values, 'code', 2, 32);
    const name = requireText(errors, values, 'name', 2, 160, 'Müşteri adı');
    const taxNumber = (values.taxNumber ?? '').trim();
    if (!/^\d{10,11}$/.test(taxNumber)) {
      errors.taxNumber = 'Vergi numarası 10 veya 11 rakamdan oluşmalıdır.';
    }

    const address = requireText(errors, values, 'address', 5, 400, 'Adres');
    return Object.keys(errors).length === 0 ? { input: { code, name, taxNumber, address }, errors } : { errors };
  },
  identify: (c) => (
    <>
      <Code>{c.code}</Code> {c.name}
    </>
  ),
  create: masterApi.createCustomer,
  update: masterApi.updateCustomer,
  setActive: masterApi.setCustomerActive,
  deactivateEffect: 'Pasif müşteriye yeni sipariş açılamaz ve taslak siparişleri onaylanamaz. Onaylı siparişler etkilenmez.',
  emptyText: 'Müşteri tanımlandığında satış siparişlerinde seçilebilir.',
};

function codeNameConfig<T extends Entity & { code: string; name: string }>(options: {
  formId: string;
  noun: string;
  caption: string;
  codeLabel: string;
  nameLabel: string;
  codeMax: number;
  nameMax: number;
  create: (input: CodeNameInput, key: string) => Promise<T>;
  update: (id: string, input: CodeNameInput, etag: string) => Promise<T>;
  setActive: (id: string, active: boolean) => Promise<T>;
  deactivateEffect: string;
  emptyText: string;
}): Config<T, CodeNameInput> {
  return {
    formId: options.formId,
    noun: options.noun,
    caption: options.caption,
    columns: [
      { id: 'code', header: 'Kod', kind: 'code', rowHeader: true, cell: (item) => <span translate="no">{item.code}</span> },
      { id: 'name', header: options.nameLabel, kind: 'name', cell: (item) => item.name },
      { id: 'state', header: 'Durum', cell: activeTag },
    ],
    fields: [
      { name: 'code', label: options.codeLabel, kind: 'code', maxLength: options.codeMax, hint: 'Kod kaydedildikten sonra değiştirilemez; ad düzenlenebilir.' },
      { name: 'name', label: options.nameLabel, kind: 'text', maxLength: options.nameMax },
    ],
    empty: { code: '', name: '' },
    toValues: (item) => ({ code: item.code, name: item.name }),
    parse: (values) => {
      const errors: Record<string, string> = {};
      const code = requireCode(errors, values, 'code', 2, options.codeMax);
      const name = requireText(errors, values, 'name', 2, options.nameMax, options.nameLabel);
      return Object.keys(errors).length === 0 ? { input: { code, name }, errors } : { errors };
    },
    identify: (item) => (
      <>
        <Code>{item.code}</Code> {item.name}
      </>
    ),
    create: options.create,
    update: options.update,
    lockedOnEdit: ['code'],
    setActive: options.setActive,
    deactivateEffect: options.deactivateEffect,
    emptyText: options.emptyText,
  };
}

const supplierConfig = codeNameConfig<Supplier>({
  formId: 'tedarikci',
  noun: 'tedarikçi',
  caption: 'Tedarikçiler',
  codeLabel: 'Tedarikçi kodu',
  nameLabel: 'Tedarikçi adı',
  codeMax: 32,
  nameMax: 160,
  create: masterApi.createSupplier,
  update: masterApi.updateSupplier,
  setActive: masterApi.setSupplierActive,
  deactivateEffect: 'Pasif tedarikçiye yeni satın alma siparişi açılamaz ve taslakları onaylanamaz. Onaylı siparişlerin mal kabulü etkilenmez.',
  emptyText: 'Tedarikçi tanımlandığında satın alma siparişlerinde seçilebilir.',
});

const warehouseConfig = codeNameConfig<Warehouse>({
  formId: 'depo',
  noun: 'depo',
  caption: 'Depolar',
  codeLabel: 'Depo kodu',
  nameLabel: 'Depo adı',
  codeMax: 16,
  nameMax: 120,
  create: masterApi.createWarehouse,
  update: masterApi.updateWarehouse,
  setActive: masterApi.setWarehouseActive,
  deactivateEffect: 'Pasif depoya yeni sipariş, transfer ve düzeltme açılamaz. Mevcut stok ve onaylı belgeler korunur.',
  emptyText: 'Depo tanımlandıktan sonra kullanıcılara depo yetkisi verilebilir.',
});

// ---------------------------------------------------------------- form
function EntityForm<T extends Entity, I>({
  config,
  item,
  onSaved,
  onClose,
  onStale,
}: {
  config: Config<T, I>;
  /** Item being edited (with its current ETag); undefined when creating. */
  item: T | undefined;
  onSaved: (result: T, created: boolean) => void | Promise<void>;
  onClose: () => void;
  onStale: () => void;
}) {
  const [values, setValues] = useState<Values>(() => (item !== undefined ? config.toValues(item) : config.empty));
  const [local, setLocal] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);
  const editing = item !== undefined;

  const save = useCommand(
    (args: { input: I; etag: string | undefined; id: string | undefined }, key: string) =>
      args.id !== undefined && args.etag !== undefined ? config.update(args.id, args.input, args.etag) : config.create(args.input, key),
    {
      onSuccess: (result) => onSaved(result, !editing),
      onProblem: () => setAttempt((n) => n + 1),
    },
  );

  const server = fieldErrorsOf(save.problem);
  if (save.problem?.code === 'conflict' || save.problem?.code === 'code_taken') {
    const key = config.fields[0]?.name ?? 'code';
    server[key] = save.problem.detail ?? 'Bu kod kullanılıyor.';
  }

  const errorOf = (name: string): string | undefined => local[name] ?? server[name];
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed = config.parse(values);
    setLocal(parsed.errors);
    save.clearProblem();
    if (parsed.input === undefined) {
      setAttempt((n) => n + 1);
      return;
    }

    void save.run({ input: parsed.input, etag: item?.eTag, id: item?.id });
  };

  const summary: SummaryItem[] = [];
  for (const field of config.fields) {
    const message = errorOf(field.name);
    if (message !== undefined) {
      summary.push({ fieldId: fieldDomId(config.formId, field.name), message: `${field.label}: ${message}` });
    }
  }

  const set = (name: string) => (value: string) => setValues((previous) => ({ ...previous, [name]: value }));
  const problem = save.problem;
  const title = editing ? `Düzenle: ${config.noun}` : `Yeni ${config.noun}`;
  return (
    <form className="region region--pad stack" onSubmit={submit} noValidate aria-labelledby={`${config.formId}-form-baslik`}>
      <h2 className="section-h section-h--flush" id={`${config.formId}-form-baslik`}>
        {title}
        {item !== undefined && <> — {config.identify(item)}</>}
      </h2>
      {problem !== null && problem.isStale && <StaleAlert what="kayıt" onReload={onStale} problem={problem} />}
      {summary.length > 0 && (
        <FormErrorSummary title={`Kaydedilmedi: ${summary.length} alanı düzeltin`} items={summary} focusKey={attempt} note="Hiçbir değişiklik kaydedilmedi." />
      )}
      {problem !== null && !problem.isStale && summary.length === 0 && (
        <ProblemAlert
          problem={problem}
          title="Kaydedilemedi"
          onRetry={problem.isRetryable ? () => document.getElementById(`${config.formId}-kaydet`)?.click() : undefined}
        />
      )}
      <div className="formgrid">
        {config.fields.map((field) => {
          const common = {
            formId: config.formId,
            name: field.name,
            label: field.label,
            value: values[field.name] ?? '',
            onChange: set(field.name),
            error: errorOf(field.name),
            hint: field.hint,
            required: true,
          };
          if (field.kind === 'select') {
            return <SelectField key={field.name} {...common} options={(field.options ?? []).map((o) => ({ value: o, label: o }))} />;
          }

          if (field.kind === 'textarea') {
            return <TextAreaField key={field.name} {...common} rows={3} maxLength={field.maxLength} fieldClassName={field.full === true ? 'field--full' : undefined} />;
          }

          if (field.kind === 'code') {
            const locked = editing && config.lockedOnEdit?.includes(field.name) === true;
            return (
              <TextField key={field.name} {...common} readOnly={locked} inputClassName="code" spellCheck={false} autoCapitalize="characters" maxLength={field.maxLength} translate="no" />
            );
          }

          if (field.kind === 'money') {
            return <TextField key={field.name} {...common} inputClassName="input--money" inputMode="decimal" placeholder="0,00" />;
          }

          if (field.kind === 'int') {
            return <TextField key={field.name} {...common} inputClassName="input--qty" inputMode="numeric" pattern="[0-9]*" />;
          }

          return <TextField key={field.name} {...common} maxLength={field.maxLength} />;
        })}
      </div>
      <div className="actions actions--end">
        <Button onClick={onClose}>Vazgeç</Button>
        <Button id={`${config.formId}-kaydet`} type="submit" variant="primary" pending={save.pending} pendingLabel="Kaydediliyor…">
          {editing ? 'Değişiklikleri kaydet' : `Yeni ${config.noun} kaydını oluştur`}
        </Button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- section
type Mode<T> = { kind: 'none' } | { kind: 'create' } | { kind: 'edit'; item: T };

function MasterSection<T extends Entity, I>({
  config,
  list,
  filters,
  filtered = false,
  onChanged,
}: {
  config: Config<T, I>;
  list: CursorList<T>;
  filters?: ReactNode;
  filtered?: boolean;
  onChanged: () => void | Promise<void>;
}) {
  const [mode, setMode] = useState<Mode<T>>({ kind: 'none' });
  const [target, setTarget] = useState<T | null>(null);
  const [notice, setNotice] = useState<ReactNode>(null);
  const deactivateStep = useDisclosure(`${config.formId}-pasif`);

  const toggle = useCommand((args: { id: string; active: boolean }) => config.setActive(args.id, args.active), {
    onSuccess: async (result, args) => {
      deactivateStep.hide();
      setTarget(null);
      setNotice(
        <>
          {config.identify(result)} {args.active ? 'etkinleştirildi.' : 'pasifleştirildi.'}
        </>,
      );
      await onChanged();
    },
  });

  // After a reload the row carries a fresh ETag; the form reads it from the list, not from its snapshot.
  const editing = mode.kind === 'edit' ? (list.items.find((item) => item.id === mode.item.id) ?? mode.item) : undefined;

  const columns: Column<T>[] = [
    ...config.columns,
    {
      id: 'actions',
      header: 'İşlemler',
      headerHidden: true,
      cell: (item) => (
        <span className="actions">
          <Button size="small" onClick={() => setMode({ kind: 'edit', item })}>
            Düzenle
          </Button>
          {item.isActive ? (
            <Button
              size="small"
              variant="danger"
              onClick={() => {
                setTarget(item);
                deactivateStep.show();
              }}
            >
              Pasifleştir…
            </Button>
          ) : (
            <Button size="small" onClick={() => void toggle.run({ id: item.id, active: true })} pending={toggle.pending} pendingLabel="Etkinleştiriliyor…">
              Etkinleştir
            </Button>
          )}
        </span>
      ),
    },
  ];

  return (
    <>
      <div className="actions">
        <Button variant={mode.kind === 'none' ? 'primary' : 'default'} icon="plus" onClick={() => setMode(mode.kind === 'create' ? { kind: 'none' } : { kind: 'create' })} aria-expanded={mode.kind === 'create'}>
          Yeni {config.noun}
        </Button>
      </div>

      <LiveRegion>{notice !== null && <SuccessNotice title="Kaydedildi">{<p>{notice}</p>}</SuccessNotice>}</LiveRegion>
      {toggle.problem !== null && <ProblemAlert problem={toggle.problem} title="Durum değiştirilemedi" />}

      {mode.kind !== 'none' && (
        <EntityForm
          key={mode.kind === 'edit' ? mode.item.id : 'yeni'}
          config={config}
          item={editing}
          onClose={() => setMode({ kind: 'none' })}
          onStale={list.refetch}
          onSaved={async (result, created) => {
            setMode({ kind: 'none' });
            setNotice(
              <>
                {config.identify(result)} {created ? 'oluşturuldu.' : 'güncellendi.'}
              </>,
            );
            await onChanged();
          }}
        />
      )}

      {target !== null && (
        <ConfirmStep
          disclosure={deactivateStep}
          confirmLabel="Pasifleştir"
          pendingLabel="Pasifleştiriliyor…"
          pending={toggle.pending}
          onConfirm={() => void toggle.run({ id: target.id, active: false })}
          question={
            <>
              <strong>{config.identify(target)} pasifleştirilsin mi?</strong> {config.deactivateEffect} Kayıt silinmez; sonradan yeniden etkinleştirilebilir.
            </>
          }
        />
      )}

      {filters}

      <ListRegion
        list={list}
        loadingLabel={`${config.caption} yükleniyor…`}
        noun={config.noun}
        empty={
          filtered ? (
            <EmptyState title={`Bu süzgeçle eşleşen ${config.noun} yok`}>Süzgeci değiştirin ya da temizleyin.</EmptyState>
          ) : (
            <EmptyState title={`Henüz ${config.noun} tanımlı değil`}>{config.emptyText}</EmptyState>
          )
        }
      >
        {(items) => <DataTable caption={config.caption} captionHidden columns={columns} rows={items} rowKey={(item) => item.id} />}
      </ListRegion>
    </>
  );
}

/** Adapts a plain array query to the list frame used by the paged product list. */
function arrayList<T>(query: UseQueryResult<T[], Error>): CursorList<T> {
  return {
    items: query.data ?? [],
    isLoading: query.isPending,
    problem: query.isError ? asProblem(query.error) : null,
    hasMore: false,
    loadingMore: false,
    loadMore: () => undefined,
    refetch: () => void query.refetch(),
    isFetching: query.isFetching,
  };
}

// ---------------------------------------------------------------- tabs
const PRODUCT_FILTER = 'urun-suzgec';

function ProductFilters({ search, state, onApply }: { search: string; state: string; onApply: (changes: Record<string, string | null>) => void }) {
  const [text, setText] = useState(search);
  return (
    <form
      className="region filters"
      role="search"
      aria-label="Ürün süzgeçleri"
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        onApply({ ara: text.trim().slice(0, 64) });
      }}
    >
      <TextField formId={PRODUCT_FILTER} name="search" label="SKU veya ürün adı" type="search" value={text} onChange={setText} spellCheck={false} maxLength={64} />
      <SelectField
        formId={PRODUCT_FILTER}
        name="active"
        label="Durum"
        value={state}
        onChange={(value) => onApply({ durum: value })}
        placeholder="Tümü"
        options={[
          { value: 'aktif', label: 'Yalnız aktif' },
          { value: 'pasif', label: 'Yalnız pasif' },
        ]}
      />
      <div className="actions">
        <Button type="submit">Ürünlerde ara</Button>
      </div>
    </form>
  );
}

function ProductsTab() {
  const { params, update } = useUrlState();
  const invalidate = useInvalidate();
  const search = params.get('ara') ?? '';
  const state = params.get('durum') ?? '';
  const active = state === 'aktif' ? true : state === 'pasif' ? false : undefined;
  const list = useCursorList<Product>(['products', search, state], (cursor, signal) =>
    masterApi.products({ search: search.length > 0 ? search : undefined, active, cursor }, signal),
  );
  return (
    <MasterSection
      config={productConfig}
      list={list}
      filtered={search.length > 0 || active !== undefined}
      filters={<ProductFilters key={search} search={search} state={state} onApply={(changes) => update(changes)} />}
      onChanged={async () => {
        await Promise.all([invalidate('products'), invalidate('product-picker')]);
      }}
    />
  );
}

function ActiveOnly({ formId, checked, onChange }: { formId: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <form className="region filters" role="search" aria-label="Süzgeç" onSubmit={(event) => event.preventDefault()}>
      <CheckField formId={formId} name="activeOnly" label="Yalnız aktif kayıtlar" checked={checked} onChange={onChange} />
    </form>
  );
}

function CustomersTab() {
  const { params, update } = useUrlState();
  const invalidate = useInvalidate();
  const activeOnly = params.get('durum') === 'aktif';
  const query = useApiQuery(['customers', activeOnly ? 'active' : 'all'], (signal) => masterApi.customers(activeOnly ? true : undefined, signal));
  return (
    <MasterSection
      config={customerConfig}
      list={arrayList(query)}
      filtered={activeOnly}
      filters={<ActiveOnly formId="musteri-suzgec" checked={activeOnly} onChange={(checked) => update({ durum: checked ? 'aktif' : null })} />}
      onChanged={() => invalidate('customers')}
    />
  );
}

function SuppliersTab() {
  const { params, update } = useUrlState();
  const invalidate = useInvalidate();
  const activeOnly = params.get('durum') === 'aktif';
  const query = useApiQuery(['suppliers', activeOnly ? 'active' : 'all'], (signal) => masterApi.suppliers(activeOnly ? true : undefined, signal));
  return (
    <MasterSection
      config={supplierConfig}
      list={arrayList(query)}
      filtered={activeOnly}
      filters={<ActiveOnly formId="tedarikci-suzgec" checked={activeOnly} onChange={(checked) => update({ durum: checked ? 'aktif' : null })} />}
      onChanged={() => invalidate('suppliers')}
    />
  );
}

function WarehousesTab() {
  const invalidate = useInvalidate();
  const query = useApiQuery(['warehouses'], (signal) => masterApi.warehouses(signal));
  return <MasterSection config={warehouseConfig} list={arrayList(query)} onChanged={() => invalidate('warehouses')} />;
}

const TABS = [
  { id: 'urunler', label: 'Ürünler' },
  { id: 'musteriler', label: 'Müşteriler' },
  { id: 'tedarikciler', label: 'Tedarikçiler' },
  { id: 'depolar', label: 'Depolar' },
] as const;

export function MasterDataScreen() {
  const { params } = useUrlState();
  const tab = TABS.find((t) => t.id === params.get('tur'))?.id ?? 'urunler';
  return (
    <div className="view">
      <PageHead
        title="Ana veri"
        lead="Ürün, müşteri, tedarikçi ve depo kayıtları. Ana veri silinmez; kullanılmayacak kayıt pasifleştirilir."
      />
      <nav className="tabs" aria-label="Ana veri türü">
        {TABS.map((t) => (
          <KLink key={t.id} to={`/yonetim/ana-veri?tur=${t.id}`} aria-current={t.id === tab ? 'page' : undefined}>
            {t.label}
          </KLink>
        ))}
      </nav>
      {tab === 'urunler' && <ProductsTab />}
      {tab === 'musteriler' && <CustomersTab />}
      {tab === 'tedarikciler' && <SuppliersTab />}
      {tab === 'depolar' && <WarehousesTab />}
    </div>
  );
}
