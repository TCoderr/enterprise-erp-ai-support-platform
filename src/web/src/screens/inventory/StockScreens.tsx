import { useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { inventoryApi } from '../../api/endpoints';
import type { Balance, Movement, MovementType, Reservation, WarehouseAccess } from '../../api/types';
import { abilitiesOf } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { BalanceStrip, stockBalance } from '../../components/BalanceStrip';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, SelectField, TextField } from '../../components/forms';
import { EmptyState } from '../../components/states';
import { Button, Code, PageHead, Qty, Tag } from '../../components/ui';
import { formatDateTime, formatQuantity, formatSigned, istanbulDayStart } from '../../lib/format';
import { MOVEMENT_LABELS, RESERVATION_LABELS, labelOf } from '../../lib/labels';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import { KLink, useWarehouse } from '../../shell/context';

/** Stock is always read for one named warehouse; without a grant there is nothing to show. */
export function NoWarehouse() {
  return (
    <EmptyState title="Hiçbir depo için yetkiniz yok" icon="lock">
      Stok ve depo belgeleri depo bazında görüntülenir. Bir depoya erişmeniz gerekiyorsa sistem yöneticinizden depo yetkisi isteyin.
    </EmptyState>
  );
}

export function WarehouseTag({ warehouse }: { warehouse: WarehouseAccess }) {
  return (
    <Tag tone="plain">
      <Code>{warehouse.code}</Code>
      {warehouse.name}
    </Tag>
  );
}

function withParams(path: string, current: URLSearchParams, changes: Record<string, string | null>): string {
  const next = new URLSearchParams(current);
  for (const [key, value] of Object.entries(changes)) {
    if (value === null) {
      next.delete(key);
    } else {
      next.set(key, value);
    }
  }

  const text = next.toString();
  return text.length > 0 ? `${path}?${text}` : path;
}

// ---------------------------------------------------------------- balances
const BALANCE_FORM = 'stok-suzgec';

function BalanceFilters({ search, low, onApply }: { search: string; low: boolean; onApply: (search: string, low: boolean) => void }) {
  const [text, setText] = useState(search);
  const [lowOnly, setLowOnly] = useState(low);
  const [error, setError] = useState<string | undefined>(undefined);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (text.trim().length > 64) {
      setError('Arama en fazla 64 karakter olabilir.');
      return;
    }

    setError(undefined);
    onApply(text.trim(), lowOnly);
  };

  return (
    <form className="region filters" role="search" aria-label="Stok süzgeçleri" onSubmit={submit} noValidate>
      <TextField
        formId={BALANCE_FORM}
        name="search"
        label="Ürün veya SKU"
        type="search"
        value={text}
        onChange={setText}
        spellCheck={false}
        maxLength={64}
        placeholder="Örn. ELK-NYM veya kablo…"
        error={error}
      />
      <CheckField formId={BALANCE_FORM} name="lowStockOnly" label="Yalnız düşük stok" checked={lowOnly} onChange={setLowOnly} />
      <div className="actions">
        <Button type="submit">Süz</Button>
        {(search.length > 0 || low) && (
          <Button
            onClick={() => {
              setText('');
              setLowOnly(false);
              onApply('', false);
            }}
          >
            Süzgeçleri temizle
          </Button>
        )}
      </div>
    </form>
  );
}

export function BalancesScreen() {
  const { selected } = useWarehouse();
  const { params, update } = useUrlState();
  const search = params.get('ara') ?? '';
  const low = params.get('dusuk') === '1';
  const productId = params.get('urun');

  const list = useCursorList<Balance>(
    ['balances', selected?.id ?? null, search, low],
    (cursor, signal) => inventoryApi.balances({ warehouseId: selected?.id ?? '', search: search.length > 0 ? search : undefined, lowStockOnly: low, cursor }, signal),
    { enabled: selected !== null },
  );

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title="Stok bakiyeleri" />
        <NoWarehouse />
      </div>
    );
  }

  const lowCount = list.items.filter((b) => b.isLow).length;
  const detail = productId !== null ? list.items.find((b) => b.productId === productId) : undefined;
  const columns: Column<Balance>[] = [
    {
      id: 'sku',
      header: 'SKU',
      kind: 'code',
      rowHeader: true,
      cell: (b) => (
        <KLink to={withParams('/depo/stok', params, { urun: b.productId })} translate="no" aria-current={b.productId === productId ? 'true' : undefined}>
          {b.sku}
        </KLink>
      ),
    },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (b) => b.name },
    { id: 'unit', header: 'Birim', cell: (b) => b.unit },
    { id: 'onHand', header: 'Eldeki', kind: 'num', cell: (b) => <Qty value={b.onHand} /> },
    { id: 'reserved', header: 'Rezerve', kind: 'num', cell: (b) => <Qty value={b.reserved} /> },
    { id: 'available', header: 'Kullanılabilir', kind: 'num', cell: (b) => <Qty value={b.available} strong /> },
    { id: 'reorder', header: 'Sipariş seviyesi', kind: 'num', cell: (b) => <Qty value={b.reorderLevel} /> },
    {
      id: 'state',
      header: 'Durum',
      cell: (b) =>
        b.isLow ? (
          <Tag tone="warn" glyph="warn">
            Düşük stok
          </Tag>
        ) : (
          <Tag tone="ok" glyph="done">
            Yeterli
          </Tag>
        ),
    },
  ];

  return (
    <div className="view">
      <PageHead
        title="Stok bakiyeleri"
        sub={
          <>
            <WarehouseTag warehouse={selected} />
            {list.items.length > 0 && (
              <span className="muted">
                {formatQuantity(list.items.length)} ürün gösteriliyor · {formatQuantity(lowCount)} tanesi düşük stokta
              </span>
            )}
          </>
        }
      />

      <BalanceFilters
        key={`${search}|${low ? '1' : '0'}`}
        search={search}
        low={low}
        onApply={(nextSearch, nextLow) => update({ ara: nextSearch, dusuk: nextLow ? '1' : null, urun: null })}
      />

      {detail !== undefined && (
        <section className="region region--pad stack" aria-labelledby="stok-detay">
          <h2 className="section-h section-h--flush pagehead__sub" id="stok-detay">
            <Code>{detail.sku}</Code>
            <span className="wrap">{detail.name}</span>
            {detail.isLow && (
              <Tag tone="warn" glyph="warn">
                Düşük stok
              </Tag>
            )}
          </h2>
          <BalanceStrip model={stockBalance(detail)} />
          <p className="small">
            {detail.isLow
              ? `Kullanılabilir miktar (${formatQuantity(detail.available)}) sipariş seviyesinde ya da altında (${formatQuantity(detail.reorderLevel)}).`
              : `Kullanılabilir miktar (${formatQuantity(detail.available)}) sipariş seviyesinin (${formatQuantity(detail.reorderLevel)}) üzerinde.`}{' '}
            Rezerve miktar, onaylı satış siparişlerinin açık miktarıdır.
          </p>
          <div className="actions">
            <KLink className="btn" to={`/depo/hareketler?urun=${detail.productId}`}>
              Bu ürünün hareketleri
            </KLink>
            <KLink className="btn" to={`/depo/rezervasyonlar?urun=${detail.productId}`}>
              Bu ürünün rezervasyonları
            </KLink>
            <KLink className="btn" to={withParams('/depo/stok', params, { urun: null })}>
              Ayrıntıyı kapat
            </KLink>
          </div>
        </section>
      )}

      <ListRegion
        list={list}
        loadingLabel="Stok bakiyeleri yükleniyor…"
        noun="ürün"
        scopeNoun="Depo"
        empty={
          search.length > 0 || low ? (
            <EmptyState title="Bu süzgeçle eşleşen bakiye yok" action={<Button onClick={() => update({ ara: null, dusuk: null, urun: null })}>Süzgeçleri temizle</Button>}>
              {low ? 'Bu depoda düşük stokta ürün bulunamadı.' : 'Aramanızla eşleşen ürün bulunamadı.'} Yalnız bu depoda hareket görmüş ürünlerin bakiyesi
              listelenir.
            </EmptyState>
          ) : (
            <EmptyState title="Bu depoda henüz stok bakiyesi yok">
              Stok, onaylı bir satın alma siparişine mal kabul yapıldığında oluşur. Mal kabul kaydedildiğinde ürün burada görünür.
            </EmptyState>
          )
        }
      >
        {(items) => (
          <DataTable caption={`${selected.code} deposundaki stok bakiyeleri`} captionHidden columns={columns} rows={items} rowKey={(b) => b.productId} />
        )}
      </ListRegion>
    </div>
  );
}

// ---------------------------------------------------------------- shared product filter note
function ProductFilterNote({ productId, sample, clearHref }: { productId: string | null; sample: { sku: string; name: string } | undefined; clearHref: string }) {
  if (productId === null) {
    return <p className="small">Tek bir ürünün kayıtlarını görmek için Stok bakiyeleri ekranında ürünü seçip buraya geçin.</p>;
  }

  return (
    <p className="pagehead__sub">
      <Tag tone="plain">
        Ürün süzgeci:{' '}
        {sample !== undefined ? (
          <>
            <Code>{sample.sku}</Code> {sample.name}
          </>
        ) : (
          'seçili ürün'
        )}
      </Tag>
      <KLink to={clearHref}>Ürün süzgecini kaldır</KLink>
    </p>
  );
}

// ---------------------------------------------------------------- movements
const MOVEMENT_TYPES: readonly MovementType[] = ['Receipt', 'Shipment', 'TransferOut', 'TransferIn', 'Adjustment'];
const MOVEMENT_FORM = 'hareket-suzgec';
const DAY = /^\d{4}-\d{2}-\d{2}$/;

function MovementFilters({
  type,
  from,
  to,
  onApply,
}: {
  type: string;
  from: string;
  to: string;
  onApply: (changes: Record<string, string | null>) => void;
}) {
  const [typeValue, setTypeValue] = useState(type);
  const [fromValue, setFromValue] = useState(from);
  const [toValue, setToValue] = useState(to);
  const [error, setError] = useState<string | undefined>(undefined);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (fromValue.length > 0 && toValue.length > 0 && fromValue > toValue) {
      setError('Bitiş tarihi başlangıçtan önce olamaz.');
      document.getElementById(`${MOVEMENT_FORM}-to`)?.focus();
      return;
    }

    setError(undefined);
    onApply({ tur: typeValue, bas: fromValue, bit: toValue });
  };

  return (
    <form className="region filters" role="search" aria-label="Hareket süzgeçleri" onSubmit={submit} noValidate>
      <SelectField
        formId={MOVEMENT_FORM}
        name="type"
        label="Hareket türü"
        value={typeValue}
        onChange={setTypeValue}
        placeholder="Tüm türler"
        options={MOVEMENT_TYPES.map((t) => ({ value: t, label: MOVEMENT_LABELS[t] }))}
      />
      <TextField formId={MOVEMENT_FORM} name="from" label="Başlangıç tarihi" type="date" value={fromValue} onChange={setFromValue} />
      <TextField formId={MOVEMENT_FORM} name="to" label="Bitiş tarihi (dahil)" type="date" value={toValue} onChange={setToValue} error={error} />
      <div className="actions">
        <Button type="submit">Süz</Button>
        {(type.length > 0 || from.length > 0 || to.length > 0) && (
          <Button
            onClick={() => {
              setTypeValue('');
              setFromValue('');
              setToValue('');
              onApply({ tur: null, bas: null, bit: null });
            }}
          >
            Süzgeçleri temizle
          </Button>
        )}
      </div>
    </form>
  );
}

function referenceCell(number: string | undefined): ReactNode {
  return number !== undefined ? <span translate="no">{number}</span> : <span className="muted">—</span>;
}

export function MovementsScreen() {
  const { selected } = useWarehouse();
  const { params, update } = useUrlState();
  const typeParam = params.get('tur') ?? '';
  const type = MOVEMENT_TYPES.find((t) => t === typeParam);
  const fromDay = DAY.test(params.get('bas') ?? '') ? (params.get('bas') ?? '') : '';
  const toDay = DAY.test(params.get('bit') ?? '') ? (params.get('bit') ?? '') : '';
  const productId = params.get('urun');
  const filtered = type !== undefined || fromDay.length > 0 || toDay.length > 0 || productId !== null;

  const list = useCursorList<Movement>(
    ['movements', selected?.id ?? null, productId, type ?? null, fromDay, toDay],
    (cursor, signal) =>
      inventoryApi.movements(
        {
          warehouseId: selected?.id ?? '',
          productId: productId ?? undefined,
          type,
          from: fromDay.length > 0 ? istanbulDayStart(fromDay) : undefined,
          // The server's upper bound is exclusive: the day after the chosen end day.
          to: toDay.length > 0 ? istanbulDayStart(toDay, 1) : undefined,
          cursor,
        },
        signal,
      ),
    { enabled: selected !== null },
  );

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title="Hareketler" />
        <NoWarehouse />
      </div>
    );
  }

  const columns: Column<Movement>[] = [
    { id: 'date', header: 'Tarih', kind: 'date', cell: (m) => formatDateTime(m.occurredAt) },
    { id: 'type', header: 'Tür', cell: (m) => labelOf(MOVEMENT_LABELS, m.type) },
    { id: 'sku', header: 'SKU', kind: 'code', cell: (m) => <span translate="no">{m.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (m) => m.name },
    { id: 'delta', header: 'Değişim', kind: 'num', cell: (m) => formatSigned(m.quantityDelta) },
    { id: 'after', header: 'Sonraki eldeki', kind: 'num', cell: (m) => <Qty value={m.onHandAfter} /> },
    { id: 'reference', header: 'Belge', kind: 'code', cell: (m) => referenceCell(m.referenceNumber) },
  ];

  return (
    <div className="view">
      <PageHead
        title="Hareketler"
        sub={<WarehouseTag warehouse={selected} />}
        lead="Eldeki miktarı değiştiren her kayıt. Hareket günlüğü değiştirilemez; düzeltme yeni bir hareketle yapılır."
      />
      <MovementFilters
        key={`${typeParam}|${fromDay}|${toDay}`}
        type={type ?? ''}
        from={fromDay}
        to={toDay}
        onApply={(changes) => update(changes)}
      />
      <ProductFilterNote productId={productId} sample={list.items[0]} clearHref={withParams('/depo/hareketler', params, { urun: null })} />

      <ListRegion
        list={list}
        loadingLabel="Hareketler yükleniyor…"
        noun="hareket"
        scopeNoun="Depo"
        empty={
          filtered ? (
            <EmptyState
              title="Bu süzgeçle eşleşen hareket yok"
              action={<Button onClick={() => update({ tur: null, bas: null, bit: null, urun: null })}>Süzgeçleri temizle</Button>}
            >
              Seçili tür, tarih aralığı ya da ürün için bu depoda hareket bulunamadı.
            </EmptyState>
          ) : (
            <EmptyState title="Bu depoda henüz stok hareketi yok">
              Mal kabul, sevkiyat, transfer ve düzeltme kayıtları burada tarih sırasıyla listelenir.
            </EmptyState>
          )
        }
      >
        {(items) => (
          <DataTable
            caption="Stok hareketleri"
            captionHidden
            layout="scroll"
            scrollLabel="Stok hareketleri tablosu"
            columns={columns}
            rows={items}
            rowKey={(m) => String(m.id)}
          />
        )}
      </ListRegion>
    </div>
  );
}

// ---------------------------------------------------------------- reservations
export function ReservationsScreen() {
  const me = useMe();
  const { selected } = useWarehouse();
  const { params, update } = useUrlState();
  const productId = params.get('urun');
  const canOpenOrders = abilitiesOf(me).sales || abilitiesOf(me).warehouseOps;

  const list = useCursorList<Reservation>(
    ['reservations', selected?.id ?? null, productId],
    (cursor, signal) => inventoryApi.reservations({ warehouseId: selected?.id ?? '', productId: productId ?? undefined, cursor }, signal),
    { enabled: selected !== null },
  );

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title="Rezervasyonlar" />
        <NoWarehouse />
      </div>
    );
  }

  const columns: Column<Reservation>[] = [
    { id: 'date', header: 'Tarih', kind: 'date', cell: (r) => formatDateTime(r.occurredAt) },
    { id: 'reason', header: 'Neden', cell: (r) => labelOf(RESERVATION_LABELS, r.reason) },
    { id: 'sku', header: 'SKU', kind: 'code', cell: (r) => <span translate="no">{r.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (r) => r.name },
    { id: 'delta', header: 'Değişim', kind: 'num', cell: (r) => formatSigned(r.quantityDelta) },
    { id: 'after', header: 'Sonraki rezerve', kind: 'num', cell: (r) => <Qty value={r.reservedAfter} /> },
    {
      id: 'order',
      header: 'Satış siparişi',
      kind: 'code',
      cell: (r) =>
        r.salesOrderNumber === undefined ? (
          <span className="muted">—</span>
        ) : canOpenOrders ? (
          <KLink to={`/satis/siparisler/${r.salesOrderId}`} translate="no">
            {r.salesOrderNumber}
          </KLink>
        ) : (
          <span translate="no">{r.salesOrderNumber}</span>
        ),
    },
  ];

  return (
    <div className="view">
      <PageHead
        title="Rezervasyonlar"
        sub={<WarehouseTag warehouse={selected} />}
        lead="Rezerve miktarı değiştiren her kayıt: onayla ayrılan, sevkle düşülen ve iptalle serbest bırakılan miktarlar. Defter değiştirilemez."
      />
      <ProductFilterNote productId={productId} sample={list.items[0]} clearHref={withParams('/depo/rezervasyonlar', params, { urun: null })} />

      <ListRegion
        list={list}
        loadingLabel="Rezervasyon kayıtları yükleniyor…"
        noun="kayıt"
        scopeNoun="Depo"
        empty={
          productId !== null ? (
            <EmptyState title="Bu ürün için rezervasyon kaydı yok" action={<Button onClick={() => update({ urun: null })}>Ürün süzgecini kaldır</Button>}>
              Seçili ürün bu depoda hiçbir satış siparişi için rezerve edilmemiş.
            </EmptyState>
          ) : (
            <EmptyState title="Bu depoda henüz rezervasyon kaydı yok">
              Bir satış siparişi onaylandığında açık miktarı rezerve edilir ve kayıt burada görünür.
            </EmptyState>
          )
        }
      >
        {(items) => (
          <DataTable
            caption="Rezervasyon defteri"
            captionHidden
            layout="scroll"
            scrollLabel="Rezervasyon defteri tablosu"
            columns={columns}
            rows={items}
            rowKey={(r) => String(r.id)}
          />
        )}
      </ListRegion>
    </div>
  );
}
