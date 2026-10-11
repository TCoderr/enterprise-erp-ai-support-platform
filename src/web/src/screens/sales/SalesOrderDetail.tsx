import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { salesApi } from '../../api/endpoints';
import type { SalesLine, SalesOrder, SalesOrderStatus, ShipmentSummary } from '../../api/types';
import { abilitiesOf, hasLevel, hasRole } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { BalanceStrip, salesBalance } from '../../components/BalanceStrip';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { LiveRegion, useDisclosure } from '../../components/forms';
import { Icon } from '../../components/Icon';
import { ProblemAlert, QueryView, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, FlowSteps, Meta, PageHead, Qty, StatusTag, linearSteps } from '../../components/ui';
import type { MetaItem } from '../../components/ui';
import { formatDateTime, formatMoney, formatQuantity } from '../../lib/format';
import { SALES_STATUS, lookOf, personLabel } from '../../lib/labels';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { KLink, useWarehouse } from '../../shell/context';
import { CancelOrder } from '../shared/CancelOrder';

const FLOW: readonly SalesOrderStatus[] = ['Draft', 'Confirmed', 'PartiallyShipped', 'Shipped'];

function sum(lines: readonly SalesLine[], pick: (line: SalesLine) => number): number {
  return lines.reduce((acc, line) => acc + pick(line), 0);
}

function nextStep(order: SalesOrder, open: number, shipped: number): string {
  const full = order.view === 'Full';
  switch (order.status) {
    case 'Draft':
      return 'Taslak: stok ayrılmadı. Onayladığınızda tüm satırlar için stok rezerve edilir — hepsi ya da hiçbiri.';
    case 'Confirmed':
      return full
        ? `Onaylandı: ${formatQuantity(open)} adet rezerve. Sevkiyatı depo görevlisi “Sevk bekleyenler” ekranından kaydeder.`
        : `Onaylandı: ${formatQuantity(open)} adet sevk bekliyor.`;
    case 'PartiallyShipped':
      return full
        ? `${formatQuantity(shipped)} adet sevk edildi; ${formatQuantity(open)} adet açık ve rezerve. Kalan sevk edilebilir ya da iptal edilebilir.`
        : `${formatQuantity(shipped)} adet sevk edildi; ${formatQuantity(open)} adet sevk bekliyor.`;
    case 'Shipped':
      return 'Siparişin tamamı sevk edildi. Belge kapalıdır ve değiştirilemez.';
    case 'Cancelled':
      return shipped > 0
        ? `Kısmen sevk edildi, kalanı iptal edildi: ${formatQuantity(shipped)} adet sevk edilmiş olarak korunur. Belge kapalıdır.`
        : 'Sipariş iptal edildi. Belge kapalıdır ve yeniden açılamaz.';
    default:
      return '';
  }
}

function SalesOrderDocument({ order, onReload, reloading }: { order: SalesOrder; onReload: () => void; reloading: boolean }) {
  const me = useMe();
  const { nameOf, codeOf } = useWarehouse();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const cancelStep = useDisclosure('siparis-iptal');
  const [notice, setNotice] = useState<{ title: string; text: string } | null>(null);

  const full = order.view === 'Full';
  const ordered = sum(order.lines, (l) => l.orderedQty);
  const shipped = sum(order.lines, (l) => l.shippedQty);
  const open = sum(order.lines, (l) => l.openQty);
  const look = lookOf(SALES_STATUS, order.status);
  const active = order.status === 'Confirmed' || order.status === 'PartiallyShipped';
  const canManage = full && (hasRole(me, 'SalesManager') || order.ownerUserId === me.id);
  const canShip = active && hasRole(me, 'WarehouseOperator') && hasLevel(me, order.warehouseId, 'Operate');
  const can = abilitiesOf(me);

  const applied = async (result: SalesOrder, title: string, text: string) => {
    setCached(['sales-order', order.id], result);
    setNotice({ title, text });
    await invalidate('sales-orders');
  };

  const confirm = useCommand((etag: string, key: string) => salesApi.confirm(order.id, etag, key), {
    onSuccess: (result) =>
      applied(result, 'Sipariş onaylandı', `${formatQuantity(sum(result.lines, (l) => l.openQty))} adet bu sipariş için rezerve edildi.`),
    onProblem: () => setNotice(null),
  });
  const cancel = useCommand((args: { reason: string; etag: string }, key: string) => salesApi.cancel(order.id, args.reason, args.etag, key), {
    onSuccess: (result) => {
      cancelStep.hide();
      const cancelled = sum(result.lines, (l) => l.cancelledQty);
      return applied(
        result,
        shipped > 0 ? 'Kalan miktar iptal edildi' : 'Sipariş iptal edildi',
        shipped > 0
          ? `${formatQuantity(cancelled)} adet iptal edildi ve rezervasyonu serbest bırakıldı; sevk edilen ${formatQuantity(shipped)} adet korundu.`
          : 'Rezervasyon varsa serbest bırakıldı; stok miktarı değişmedi.',
      );
    },
    onProblem: () => setNotice(null),
  });

  const problem = confirm.problem ?? cancel.problem;
  const stale = problem !== null && problem.isStale;
  const insufficient = confirm.problem !== null && confirm.problem.code === 'insufficient_available';

  // 409 insufficient_available: "lines[N]" is the zero-based line position (LineNo − 1).
  const shortages = new Map<string, string>();
  if (insufficient && confirm.problem !== null) {
    for (const error of confirm.problem.errors) {
      const match = /^lines\[(\d+)\]/.exec(error.field);
      const line = match ? order.lines.find((l) => l.lineNo === Number(match[1]) + 1) : undefined;
      if (line !== undefined) {
        shortages.set(line.id, error.detail);
      }
    }
  }

  const reload = () => {
    confirm.clearProblem();
    cancel.clearProblem();
    onReload();
  };

  const meta: MetaItem[] = [
    {
      label: 'Müşteri',
      value: (
        <>
          {order.customerName} <Code muted>{order.customerCode}</Code>
        </>
      ),
    },
    {
      label: 'Depo',
      value: (
        <>
          {nameOf(order.warehouseId)} {codeOf(order.warehouseId) !== undefined && <Code muted>{codeOf(order.warehouseId)}</Code>}
        </>
      ),
    },
    { label: 'Sahip', value: personLabel(order.ownerDisplayName, order.ownerUserId, me.id) },
    { label: 'Oluşturma', value: <span className="date">{formatDateTime(order.createdAt)}</span> },
  ];
  if (order.confirmedAt !== undefined) {
    meta.push({ label: 'Onay tarihi', value: <span className="date">{formatDateTime(order.confirmedAt)}</span> });
  }

  if (order.closedAt !== undefined) {
    meta.push({ label: 'Kapanış', value: <span className="date">{formatDateTime(order.closedAt)}</span> });
  }

  if (order.shippingAddress.length > 0) {
    meta.push({ label: 'Sevk adresi', value: order.shippingAddress });
  }

  if (order.customerTaxNumber !== undefined) {
    meta.push({ label: 'Vergi no', value: <Code>{order.customerTaxNumber}</Code> });
  }

  if (order.note !== undefined) {
    meta.push({ label: 'Not', value: <span className="pretext">{order.note}</span> });
  }

  if (order.cancelReason !== undefined) {
    meta.push({ label: 'İptal gerekçesi', value: <span className="pretext">{order.cancelReason}</span> });
  }

  const lineColumns: Column<SalesLine>[] = [
    { id: 'sku', header: 'SKU', kind: 'code', rowHeader: true, cell: (l) => <span translate="no">{l.sku}</span> },
    {
      id: 'name',
      header: 'Ürün',
      kind: 'name',
      cell: (l) => {
        const shortage = shortages.get(l.id);
        return shortage === undefined ? (
          l.productName
        ) : (
          <span className="stack">
            <span>{l.productName}</span>
            <span className="field-error">
              <Icon name="error" />
              Stok yetersiz: {shortage}
            </span>
          </span>
        );
      },
    },
    { id: 'unit', header: 'Birim', cell: (l) => l.unit },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (l) => <Qty value={l.orderedQty} /> },
    { id: 'shipped', header: 'Sevk edilen', kind: 'num', cell: (l) => <Qty value={l.shippedQty} /> },
    { id: 'cancelled', header: 'İptal', kind: 'num', cell: (l) => <Qty value={l.cancelledQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (l) => <Qty value={l.openQty} /> },
  ];
  // The fulfilment view carries no prices: the columns are absent, not blanked.
  if (full) {
    lineColumns.push(
      { id: 'price', header: 'Birim fiyat', kind: 'num', cell: (l) => (l.unitPrice !== undefined ? formatMoney(l.unitPrice) : '—') },
      { id: 'total', header: 'Tutar', kind: 'num', cell: (l) => (l.lineTotal !== undefined ? formatMoney(l.lineTotal) : '—') },
    );
  }

  const shipmentColumns: Column<ShipmentSummary>[] = [
    { id: 'number', header: 'Sevkiyat no', kind: 'code', rowHeader: true, cell: (s) => <span translate="no">{s.number}</span> },
    { id: 'reference', header: 'Sevk referansı', kind: 'code', cell: (s) => <span translate="no">{s.dispatchReference}</span> },
    { id: 'date', header: 'Tarih', kind: 'date', cell: (s) => formatDateTime(s.createdAt) },
    { id: 'quantity', header: 'Miktar', kind: 'num', cell: (s) => <Qty value={s.totalQuantity} /> },
  ];

  const stoppedAt: SalesOrderStatus | undefined =
    order.status === 'Cancelled' ? (shipped > 0 ? 'PartiallyShipped' : order.confirmedAt !== undefined ? 'Confirmed' : 'Draft') : undefined;

  return (
    <>
      <PageHead
        title="Satış siparişi"
        plate={order.number}
        sub={
          <>
            <StatusTag
              look={look}
              note={order.status === 'Cancelled' && shipped > 0 ? `${formatQuantity(shipped)}/${formatQuantity(ordered)} sevk edildi` : undefined}
            />
            {!full && <span className="muted">Depo görünümü: fiyat ve ticari notlar gösterilmez.</span>}
          </>
        }
        lead={nextStep(order, open, shipped)}
        meta={<Meta items={meta} />}
      />

      <div>
        <FlowSteps label="Sipariş akışı" steps={linearSteps(FLOW, SALES_STATUS, order.status, stoppedAt)} />
        {order.status === 'Cancelled' && (
          <p className="small">
            Sonuç: iptal edildi{order.closedAt !== undefined ? ` (${formatDateTime(order.closedAt)})` : ''}.
          </p>
        )}
      </div>

      <LiveRegion>
        {notice !== null && (
          <SuccessNotice title={notice.title}>
            <p>{notice.text}</p>
          </SuccessNotice>
        )}
      </LiveRegion>

      {stale && problem !== null && <StaleAlert what="sipariş" onReload={reload} pending={reloading} problem={problem} />}
      {insufficient && confirm.problem !== null && (
        <ProblemAlert
          problem={confirm.problem}
          title={`Sipariş onaylanamadı: ${shortages.size > 0 ? `${shortages.size} satırda ` : ''}stok yetersiz`}
          hideFieldErrors
        >
          <ul>
            {order.lines
              .filter((l) => shortages.has(l.id))
              .map((l) => (
                <li key={l.id}>
                  <Code>{l.sku}</Code> {l.productName} — {shortages.get(l.id)}.
                </li>
              ))}
          </ul>
          <p>Hiçbir satır rezerve edilmedi; sipariş taslak olarak duruyor. Miktarı düşürün veya mal kabulünden sonra yeniden onaylayın.</p>
          {canManage && (
            <div className="actions">
              <KLink className="btn" to={`/satis/siparisler/${order.id}/duzenle`}>
                Taslağı düzenle
              </KLink>
            </div>
          )}
        </ProblemAlert>
      )}
      {problem !== null && !stale && !insufficient && (
        <ProblemAlert
          problem={problem}
          title={confirm.problem !== null ? 'Sipariş onaylanamadı' : 'Sipariş iptal edilemedi'}
          onRetry={problem.code === 'invalid_state' ? reload : undefined}
          retryLabel="Güncel siparişi yükle"
          retryPending={reloading}
          hideFieldErrors={cancel.problem !== null && cancel.problem.errors.some((e) => e.field === 'reason')}
        />
      )}

      <section className="region balance" aria-labelledby="siparis-denge">
        <h2 className="sr-only" id="siparis-denge">
          Miktar dengesi
        </h2>
        <BalanceStrip model={salesBalance(order.lines, order.status !== 'Draft')} />
      </section>

      <section className="region tablewrap">
        <DataTable
          caption="Satırlar"
          captionNote={`${order.lines.length} satır${full ? ' · fiyatlar satır yazıldığı andaki liste fiyatıdır' : ''}`}
          columns={lineColumns}
          rows={order.lines}
          rowKey={(l) => l.id}
          footer={{
            label: 'Toplam',
            span: 3,
            cells: {
              ordered: <Qty value={ordered} />,
              shipped: <Qty value={shipped} />,
              cancelled: <Qty value={sum(order.lines, (l) => l.cancelledQty)} />,
              open: <Qty value={open} />,
              total: order.total !== undefined ? formatMoney(order.total) : undefined,
            },
          }}
        />
      </section>

      <section className="region tablewrap">
        {order.shipments.length > 0 ? (
          <DataTable
            caption="Sevkiyatlar"
            captionNote={`${order.shipments.length} sevkiyat · toplam ${formatQuantity(shipped)}`}
            columns={shipmentColumns}
            rows={order.shipments}
            rowKey={(s) => s.id}
          />
        ) : (
          <div className="region--pad">
            <h2 className="section-h">Sevkiyatlar</h2>
            <p className="small">
              {order.status === 'Draft'
                ? 'Taslak sipariş sevk edilemez; önce onaylanmalıdır.'
                : 'Bu sipariş için henüz sevkiyat kaydedilmedi.'}
            </p>
          </div>
        )}
      </section>

      {canManage && (order.status === 'Draft' || active) && (
        <CancelOrder
          disclosure={cancelStep}
          formId="siparis-iptal-form"
          openQty={open}
          fulfilledQty={shipped}
          fulfilledWord="sevk edilen"
          releasesReservation={active}
          pending={cancel.pending}
          serverError={cancel.problem?.errors.find((e) => e.field === 'reason')?.detail}
          onConfirm={(reason) => void cancel.run({ reason, etag: order.eTag })}
        />
      )}

      <div className="actions actions--sticky">
        <KLink className="btn" to={`/satis/siparisler/${order.id}/ozet`}>
          <Icon name="print" />
          Yazdırılabilir özet
        </KLink>
        {canManage && order.status === 'Draft' && (
          <KLink className="btn" to={`/satis/siparisler/${order.id}/duzenle`}>
            Taslağı düzenle
          </KLink>
        )}
        <span className="spacer" />
        {canManage && (order.status === 'Draft' || active) && (
          <Button
            variant="danger"
            aria-expanded={cancelStep.open}
            aria-controls={cancelStep.panelId}
            onClick={cancelStep.toggle}
          >
            {shipped > 0 ? `Kalanı iptal et (${formatQuantity(open)})…` : 'Siparişi iptal et…'}
          </Button>
        )}
        {canManage && order.status === 'Draft' && (
          <Button variant="primary" onClick={() => void confirm.run(order.eTag)} pending={confirm.pending} pendingLabel="Onaylanıyor…">
            Siparişi onayla
          </Button>
        )}
        {canShip && (
          <KLink className="btn btn--primary" to={`/depo/sevk/${order.id}`}>
            Sevkiyat gir
          </KLink>
        )}
      </div>
      {full && active && !canShip && (
        <p className="small">Sevkiyat, depo görevlisinin “Sevk bekleyenler” ekranından kaydedilir. Yazdırılan özet fatura veya irsaliye değildir.</p>
      )}
      {!can.sales && !canShip && active && <p className="small">Bu depoda sevkiyat kaydetmek için “İşlem” seviyesinde depo yetkisi gerekir.</p>}
    </>
  );
}

export function SalesOrderDetailScreen() {
  const { id = '' } = useParams();
  const me = useMe();
  const query = useApiQuery(['sales-order', id], (signal) => salesApi.get(id, signal));
  const back = abilitiesOf(me).sales ? (
    <KLink className="btn" to="/satis/siparisler">
      Siparişlere dön
    </KLink>
  ) : (
    <KLink className="btn" to="/depo/sevk">
      Sevk bekleyenlere dön
    </KLink>
  );

  return (
    <div className="view">
      <QueryView query={query} loadingLabel="Sipariş yükleniyor…" what="Sipariş" notFoundAction={back}>
        {(order) => <SalesOrderDocument order={order} onReload={() => void query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}
