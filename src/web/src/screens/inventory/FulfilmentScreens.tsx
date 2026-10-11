import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { purchasingApi, salesApi } from '../../api/endpoints';
import type { Page, PurchaseOrder, PurchaseOrderListItem, Receipt, SalesOrder, SalesOrderListItem, Shipment } from '../../api/types';
import { hasLevel, hasRole } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { BalanceStrip, purchaseBalance, salesBalance } from '../../components/BalanceStrip';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { LiveRegion } from '../../components/forms';
import { EmptyState, InfoNote, LoadingRegion, QueryView, SuccessNotice } from '../../components/states';
import { Code, Meta, PageHead, Qty, StatusTag } from '../../components/ui';
import { formatDateTime, formatQuantity } from '../../lib/format';
import { PURCHASE_STATUS, SALES_STATUS, lookOf } from '../../lib/labels';
import type { StatusLook } from '../../lib/labels';
import { ListRegion, useCursorList } from '../../lib/lists';
import { useApiQuery, useInvalidate } from '../../lib/query';
import { KLink, useGo, useWarehouse } from '../../shell/context';
import { FulfilmentForm } from '../shared/FulfilmentForm';
import { NoWarehouse, WarehouseTag } from './StockScreens';

// ---------------------------------------------------------------- queues
interface QueueRow {
  id: string;
  number: string;
  party: string;
  status: string;
  createdAt: string;
  orderedQty: number;
  doneQty: number;
  openQty: number;
}

interface QueueText {
  title: string;
  lead: string;
  party: string;
  done: string;
  action: string;
  path: string;
  firstCaption: string;
  secondCaption: string;
  firstEmpty: string;
  secondEmpty: string;
  emptyTitle: string;
  emptyText: string;
  loading: string;
}

const QUEUE: Record<'shipment' | 'receipt', QueueText> = {
  shipment: {
    title: 'Sevk bekleyenler',
    lead: 'Onaylanmış ve açık miktarı kalan satış siparişleri. Depo görünümünde fiyat yoktur.',
    party: 'Müşteri',
    done: 'Sevk edilen',
    action: 'Sevkiyat gir',
    path: '/depo/sevk',
    firstCaption: 'Onaylandı — hiç sevk edilmedi',
    secondCaption: 'Kısmen sevk edildi — kalan bekliyor',
    firstEmpty: 'Hiç sevk edilmemiş onaylı sipariş yok.',
    secondEmpty: 'Kısmen sevk edilip kalanı bekleyen sipariş yok.',
    emptyTitle: 'Bu depoda sevk bekleyen sipariş yok',
    emptyText: 'Onaylanan satış siparişleri burada listelenir. Başka bir depoya bakmak için üstteki depo seçiciyi kullanın.',
    loading: 'Sevk bekleyen siparişler yükleniyor…',
  },
  receipt: {
    title: 'Kabul bekleyenler',
    lead: 'Onaylanmış ve teslimi beklenen satın alma siparişleri. Depo görünümünde maliyet yoktur.',
    party: 'Tedarikçi',
    done: 'Kabul edilen',
    action: 'Mal kabul gir',
    path: '/depo/kabul',
    firstCaption: 'Onaylandı — hiç kabul edilmedi',
    secondCaption: 'Kısmen kabul edildi — kalan bekleniyor',
    firstEmpty: 'Hiç kabul edilmemiş onaylı sipariş yok.',
    secondEmpty: 'Kısmen kabul edilip kalanı beklenen sipariş yok.',
    emptyTitle: 'Bu depoda kabul bekleyen sipariş yok',
    emptyText: 'Onaylanan satın alma siparişleri burada listelenir. Başka bir depoya bakmak için üstteki depo seçiciyi kullanın.',
    loading: 'Kabul bekleyen siparişler yükleniyor…',
  },
};

function fromSales(page: Page<SalesOrderListItem>): Page<QueueRow> {
  const items = page.items.map((o) => ({
    id: o.id,
    number: o.number,
    party: o.customerName,
    status: o.status,
    createdAt: o.createdAt,
    orderedQty: o.orderedQty,
    doneQty: o.shippedQty,
    openQty: o.openQty,
  }));
  return page.nextCursor !== undefined ? { items, nextCursor: page.nextCursor } : { items };
}

function fromPurchases(page: Page<PurchaseOrderListItem>): Page<QueueRow> {
  const items = page.items.map((o) => ({
    id: o.id,
    number: o.number,
    party: o.supplierName,
    status: o.status,
    createdAt: o.createdAt,
    orderedQty: o.orderedQty,
    doneQty: o.receivedQty,
    openQty: o.openQty,
  }));
  return page.nextCursor !== undefined ? { items, nextCursor: page.nextCursor } : { items };
}

/** Work list of the warehouse operator: documents waiting for a dispatch or a goods receipt. */
function FulfilmentQueue({ kind }: { kind: 'shipment' | 'receipt' }) {
  const t = QUEUE[kind];
  const { selected } = useWarehouse();
  const warehouseId = selected?.id ?? '';
  const looks = (kind === 'shipment' ? SALES_STATUS : PURCHASE_STATUS) as Record<string, StatusLook>;

  const fetchPage = (status: 'first' | 'second') => async (cursor: string | undefined, signal: AbortSignal) =>
    kind === 'shipment'
      ? fromSales(await salesApi.list({ warehouseId, status: status === 'first' ? 'Confirmed' : 'PartiallyShipped', cursor }, signal))
      : fromPurchases(await purchasingApi.list({ warehouseId, status: status === 'first' ? 'Approved' : 'PartiallyReceived', cursor }, signal));

  const first = useCursorList<QueueRow>([kind === 'shipment' ? 'sales-orders' : 'purchase-orders', 'queue', warehouseId, 'first'], fetchPage('first'), {
    enabled: selected !== null,
    pageParam: 'sayfa1',
  });
  const second = useCursorList<QueueRow>([kind === 'shipment' ? 'sales-orders' : 'purchase-orders', 'queue', warehouseId, 'second'], fetchPage('second'), {
    enabled: selected !== null,
    pageParam: 'sayfa2',
  });

  if (selected === null) {
    return (
      <div className="view">
        <PageHead title={t.title} />
        <NoWarehouse />
      </div>
    );
  }

  const columns: Column<QueueRow>[] = [
    {
      id: 'number',
      header: 'Sipariş no',
      kind: 'code',
      rowHeader: true,
      cell: (o) => (
        <KLink to={`${t.path}/${o.id}`} translate="no">
          {o.number}
        </KLink>
      ),
    },
    { id: 'party', header: t.party, kind: 'name', cell: (o) => o.party },
    { id: 'status', header: 'Durum', cell: (o) => <StatusTag look={lookOf(looks, o.status)} /> },
    { id: 'date', header: 'Oluşturma', kind: 'date', cell: (o) => formatDateTime(o.createdAt) },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (o) => <Qty value={o.orderedQty} /> },
    { id: 'done', header: t.done, kind: 'num', cell: (o) => <Qty value={o.doneQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (o) => <Qty value={o.openQty} strong /> },
    {
      id: 'action',
      header: 'İşlem',
      headerHidden: true,
      cell: (o) => (
        <KLink className="btn btn--small" to={`${t.path}/${o.id}`} aria-label={`${o.number}: ${t.action}`}>
          {t.action}
        </KLink>
      ),
    },
  ];

  const bothEmpty =
    !first.isLoading && !second.isLoading && first.problem === null && second.problem === null && first.items.length === 0 && second.items.length === 0;

  return (
    <div className="view">
      <PageHead title={t.title} sub={<WarehouseTag warehouse={selected} />} lead={t.lead} />
      {first.isLoading || second.isLoading ? (
        <LoadingRegion label={t.loading} />
      ) : bothEmpty ? (
        <EmptyState
          title={t.emptyTitle}
          action={
            <KLink className="btn" to="/depo/stok">
              Stok bakiyelerine git
            </KLink>
          }
        >
          {t.emptyText}
        </EmptyState>
      ) : (
        <>
          <ListRegion list={second} loadingLabel={t.loading} noun="sipariş" scopeNoun="Depo" empty={<p className="region region--pad small">{t.secondEmpty}</p>}>
            {(items) => <DataTable caption={t.secondCaption} columns={columns} rows={items} rowKey={(o) => o.id} />}
          </ListRegion>
          <ListRegion list={first} loadingLabel={t.loading} noun="sipariş" scopeNoun="Depo" empty={<p className="region region--pad small">{t.firstEmpty}</p>}>
            {(items) => <DataTable caption={t.firstCaption} columns={columns} rows={items} rowKey={(o) => o.id} />}
          </ListRegion>
        </>
      )}
    </div>
  );
}

export function DispatchQueueScreen() {
  return <FulfilmentQueue kind="shipment" />;
}

export function ReceiveQueueScreen() {
  return <FulfilmentQueue kind="receipt" />;
}

// ---------------------------------------------------------------- dispatch entry
interface Posted {
  by: string;
  number: string;
  reference: string;
  quantity: number;
  status: string;
}

function DispatchDocument({ order, onReload, reloading }: { order: SalesOrder; onReload: () => Promise<unknown>; reloading: boolean }) {
  const me = useMe();
  const go = useGo();
  const invalidate = useInvalidate();
  const { nameOf, codeOf } = useWarehouse();
  const [posted, setPosted] = useState<Posted | null>(null);
  const open = order.lines.reduce((acc, l) => acc + l.openQty, 0);
  const active = order.status === 'Confirmed' || order.status === 'PartiallyShipped';
  const allowed = hasRole(me, 'WarehouseOperator') && hasLevel(me, order.warehouseId, 'Operate');

  const onPosted = async (document: Shipment | Receipt) => {
    const shipment = document as Shipment;
    setPosted({
      by: shipment.createdByDisplayName,
      number: shipment.number,
      reference: shipment.dispatchReference,
      quantity: shipment.lines.reduce((acc, l) => acc + l.quantity, 0),
      status: shipment.salesOrderStatus,
    });
    await Promise.all([onReload(), invalidate('sales-orders'), invalidate('balances'), invalidate('movements'), invalidate('reservations')]);
  };

  return (
    <>
      <PageHead
        title="Sevkiyat girişi"
        plate={order.number}
        sub={<StatusTag look={lookOf(SALES_STATUS, order.status)} />}
        meta={
          <Meta
            items={[
              {
                label: 'Müşteri',
                value: (
                  <>
                    {order.customerName} <Code muted>{order.customerCode}</Code>
                  </>
                ),
              },
              { label: 'Sevk adresi', value: order.shippingAddress.length > 0 ? order.shippingAddress : '—' },
              {
                label: 'Depo',
                value: (
                  <>
                    {nameOf(order.warehouseId)} {codeOf(order.warehouseId) !== undefined && <Code muted>{codeOf(order.warehouseId)}</Code>}
                  </>
                ),
              },
            ]}
          />
        }
        actions={
          <KLink className="btn" to={`/satis/siparisler/${order.id}`}>
            Sipariş belgesini aç
          </KLink>
        }
      />

      <LiveRegion>
        {posted !== null && (
          <SuccessNotice title={`Sevkiyat ${posted.number} kaydedildi`}>
            <p>
              <Code>{posted.reference}</Code> referansıyla {formatQuantity(posted.quantity)} adet stoktan düşüldü (kaydeden: {posted.by}). Sipariş durumu:{' '}
              <strong>{lookOf(SALES_STATUS, posted.status).label}</strong>
              {posted.status === 'PartiallyShipped' ? ` — ${formatQuantity(open)} adet açık kaldı; kalan için yeni bir sevkiyat girebilirsiniz.` : '.'}
            </p>
          </SuccessNotice>
        )}
      </LiveRegion>

      <section className="region balance" aria-labelledby="sevk-denge">
        <h2 className="sr-only" id="sevk-denge">
          Miktar dengesi
        </h2>
        <BalanceStrip model={salesBalance(order.lines, true)} />
      </section>

      {!allowed ? (
        <InfoNote tone="warn" title="Bu depoda sevkiyat kaydetme yetkiniz yok">
          <p>Sevkiyatı, bu depoda “İşlem” seviyesinde yetkisi olan depo görevlisi kaydeder.</p>
        </InfoNote>
      ) : active ? (
        <FulfilmentForm
          kind="shipment"
          orderId={order.id}
          rows={order.lines.map((l) => ({
            id: l.id,
            sku: l.sku,
            productName: l.productName,
            unit: l.unit,
            orderedQty: l.orderedQty,
            doneQty: l.shippedQty,
            openQty: l.openQty,
          }))}
          onPosted={onPosted}
          onReload={() => void onReload()}
          reloading={reloading}
          onCancel={() => go('/depo/sevk')}
        />
      ) : (
        <InfoNote title="Bu sipariş için sevkiyat girilemez">
          <p>
            Sipariş “{lookOf(SALES_STATUS, order.status).label}” durumunda; açık miktar kalmadı ya da sipariş kapatıldı.{' '}
            <KLink to="/depo/sevk">Sevk bekleyenlere dön</KLink>
          </p>
        </InfoNote>
      )}
    </>
  );
}

export function DispatchScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['sales-order', id], (signal) => salesApi.get(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Sipariş yükleniyor…"
        what="Sipariş"
        notFoundAction={
          <KLink className="btn" to="/depo/sevk">
            Sevk bekleyenlere dön
          </KLink>
        }
      >
        {(order) => <DispatchDocument order={order} onReload={() => query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}

// ---------------------------------------------------------------- goods receipt entry
function ReceiveDocument({ order, onReload, reloading }: { order: PurchaseOrder; onReload: () => Promise<unknown>; reloading: boolean }) {
  const me = useMe();
  const go = useGo();
  const invalidate = useInvalidate();
  const { nameOf, codeOf } = useWarehouse();
  const [posted, setPosted] = useState<Posted | null>(null);
  const open = order.lines.reduce((acc, l) => acc + l.openQty, 0);
  const active = order.status === 'Approved' || order.status === 'PartiallyReceived';
  const allowed = hasRole(me, 'WarehouseOperator') && hasLevel(me, order.warehouseId, 'Operate');

  const onPosted = async (document: Shipment | Receipt) => {
    const receipt = document as Receipt;
    setPosted({
      by: receipt.createdByDisplayName,
      number: receipt.number,
      reference: receipt.externalReference,
      quantity: receipt.lines.reduce((acc, l) => acc + l.quantity, 0),
      status: receipt.purchaseOrderStatus,
    });
    await Promise.all([onReload(), invalidate('purchase-orders'), invalidate('balances'), invalidate('movements')]);
  };

  return (
    <>
      <PageHead
        title="Mal kabul girişi"
        plate={order.number}
        sub={<StatusTag look={lookOf(PURCHASE_STATUS, order.status)} />}
        meta={
          <Meta
            items={[
              {
                label: 'Tedarikçi',
                value: (
                  <>
                    {order.supplierName} <Code muted>{order.supplierCode}</Code>
                  </>
                ),
              },
              {
                label: 'Teslim deposu',
                value: (
                  <>
                    {nameOf(order.warehouseId)} {codeOf(order.warehouseId) !== undefined && <Code muted>{codeOf(order.warehouseId)}</Code>}
                  </>
                ),
              },
              { label: 'Onay tarihi', value: <span className="date">{formatDateTime(order.approvedAt)}</span> },
            ]}
          />
        }
        actions={
          <KLink className="btn" to={`/satin-alma/siparisler/${order.id}`}>
            Sipariş belgesini aç
          </KLink>
        }
      />

      <LiveRegion>
        {posted !== null && (
          <SuccessNotice title={`Mal kabul ${posted.number} kaydedildi`}>
            <p>
              <Code>{posted.reference}</Code> irsaliyesiyle {formatQuantity(posted.quantity)} adet stoğa eklendi (kaydeden: {posted.by}). Sipariş durumu:{' '}
              <strong>{lookOf(PURCHASE_STATUS, posted.status).label}</strong>
              {posted.status === 'PartiallyReceived'
                ? ` — ${formatQuantity(open)} adet açık kaldı; kalan teslimat geldiğinde yeni bir kabul girebilirsiniz.`
                : '.'}
            </p>
          </SuccessNotice>
        )}
      </LiveRegion>

      <section className="region balance" aria-labelledby="kabul-denge">
        <h2 className="sr-only" id="kabul-denge">
          Miktar dengesi
        </h2>
        <BalanceStrip model={purchaseBalance(order.lines)} />
      </section>

      {!allowed ? (
        <InfoNote tone="warn" title="Bu depoda mal kabul kaydetme yetkiniz yok">
          <p>Mal kabulü, bu depoda “İşlem” seviyesinde yetkisi olan depo görevlisi kaydeder.</p>
        </InfoNote>
      ) : active ? (
        <FulfilmentForm
          kind="receipt"
          orderId={order.id}
          rows={order.lines.map((l) => ({
            id: l.id,
            sku: l.sku,
            productName: l.productName,
            unit: l.unit,
            orderedQty: l.orderedQty,
            doneQty: l.receivedQty,
            openQty: l.openQty,
          }))}
          onPosted={onPosted}
          onReload={() => void onReload()}
          reloading={reloading}
          onCancel={() => go('/depo/kabul')}
        />
      ) : (
        <InfoNote title="Bu sipariş için mal kabul girilemez">
          <p>
            Sipariş “{lookOf(PURCHASE_STATUS, order.status).label}” durumunda; açık miktar kalmadı ya da sipariş kapatıldı.{' '}
            <KLink to="/depo/kabul">Kabul bekleyenlere dön</KLink>
          </p>
        </InfoNote>
      )}
    </>
  );
}

export function ReceiveScreen() {
  const { id = '' } = useParams();
  const query = useApiQuery(['purchase-order', id], (signal) => purchasingApi.get(id, signal));
  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Satın alma siparişi yükleniyor…"
        what="Sipariş"
        notFoundAction={
          <KLink className="btn" to="/depo/kabul">
            Kabul bekleyenlere dön
          </KLink>
        }
      >
        {(order) => <ReceiveDocument order={order} onReload={() => query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}
