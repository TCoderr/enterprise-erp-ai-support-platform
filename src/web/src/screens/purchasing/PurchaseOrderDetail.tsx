import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { purchasingApi } from '../../api/endpoints';
import type { PurchaseLine, PurchaseOrder, PurchaseOrderStatus, ReceiptSummary } from '../../api/types';
import { abilitiesOf, hasLevel, hasRole } from '../../auth/permissions';
import { useMe } from '../../auth/session';
import { BalanceStrip, purchaseBalance } from '../../components/BalanceStrip';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { LiveRegion, useDisclosure } from '../../components/forms';
import { InfoNote, ProblemAlert, QueryView, StaleAlert, SuccessNotice } from '../../components/states';
import { Button, Code, FlowSteps, Meta, PageHead, Qty, StatusTag, linearSteps } from '../../components/ui';
import type { MetaItem } from '../../components/ui';
import { formatDateTime, formatMoney, formatQuantity } from '../../lib/format';
import { PURCHASE_STATUS, lookOf, personLabel } from '../../lib/labels';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { useCommand } from '../../lib/useCommand';
import { KLink, useWarehouse } from '../../shell/context';
import { CancelOrder } from '../shared/CancelOrder';

const FLOW: readonly PurchaseOrderStatus[] = ['Draft', 'Approved', 'PartiallyReceived', 'Received'];

function sum(lines: readonly PurchaseLine[], pick: (line: PurchaseLine) => number): number {
  return lines.reduce((acc, line) => acc + pick(line), 0);
}

function nextStep(order: PurchaseOrder, open: number, received: number): string {
  switch (order.status) {
    case 'Draft':
      return 'Taslak: tedarikçiye verilmiş sayılmaz. Onay, toplamı onay limitinin içinde olan bir satın alma uzmanı tarafından verilir.';
    case 'Approved':
      return `Onaylandı: ${formatQuantity(open)} adet teslim bekleniyor. Mal kabulü depo görevlisi “Kabul bekleyenler” ekranından kaydeder.`;
    case 'PartiallyReceived':
      return `${formatQuantity(received)} adet kabul edildi; ${formatQuantity(open)} adet açık. Kalan teslim alınabilir ya da iptal edilebilir.`;
    case 'Received':
      return 'Siparişin tamamı teslim alındı. Belge kapalıdır ve değiştirilemez.';
    case 'Cancelled':
      return received > 0
        ? `Kısmen kabul edildi, kalanı iptal edildi: ${formatQuantity(received)} adet kabul edilmiş olarak korunur. Belge kapalıdır.`
        : 'Sipariş iptal edildi. Belge kapalıdır ve yeniden açılamaz.';
    default:
      return '';
  }
}

function PurchaseOrderDocument({ order, onReload, reloading }: { order: PurchaseOrder; onReload: () => void; reloading: boolean }) {
  const me = useMe();
  const { nameOf, codeOf } = useWarehouse();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const cancelStep = useDisclosure('po-iptal');
  const [notice, setNotice] = useState<{ title: string; text: string } | null>(null);

  const full = order.view === 'Full';
  const ordered = sum(order.lines, (l) => l.orderedQty);
  const received = sum(order.lines, (l) => l.receivedQty);
  const open = sum(order.lines, (l) => l.openQty);
  const active = order.status === 'Approved' || order.status === 'PartiallyReceived';
  const mine = order.ownerUserId === me.id;
  const buyer = full && hasRole(me, 'PurchasingAgent');
  const canEdit = buyer && mine && order.status === 'Draft';
  const canApprove = buyer && order.status === 'Draft';
  const canCancel = buyer && (mine || me.poApprovalLimit > 0) && (order.status === 'Draft' || active);
  const canReceive = active && hasRole(me, 'WarehouseOperator') && hasLevel(me, order.warehouseId, 'Operate');
  const overLimit = canApprove && order.total !== undefined && order.total > me.poApprovalLimit;

  const applied = async (result: PurchaseOrder, title: string, text: string) => {
    setCached(['purchase-order', order.id], result);
    setNotice({ title, text });
    await invalidate('purchase-orders');
  };

  const approve = useCommand((etag: string, key: string) => purchasingApi.approve(order.id, etag, key), {
    onSuccess: (result) => applied(result, 'Satın alma siparişi onaylandı', 'Sipariş “Kabul bekleyenler” listesine düştü; stok, mal kabul kaydedildiğinde artar.'),
    onProblem: () => setNotice(null),
  });
  const cancel = useCommand((args: { reason: string; etag: string }, key: string) => purchasingApi.cancel(order.id, args.reason, args.etag, key), {
    onSuccess: (result) => {
      cancelStep.hide();
      return applied(
        result,
        received > 0 ? 'Kalan miktar iptal edildi' : 'Satın alma siparişi iptal edildi',
        received > 0
          ? `${formatQuantity(sum(result.lines, (l) => l.cancelledQty))} adet iptal edildi; kabul edilen ${formatQuantity(received)} adet korundu. Stok değişmedi.`
          : 'Stok değişmedi.',
      );
    },
    onProblem: () => setNotice(null),
  });

  const problem = approve.problem ?? cancel.problem;
  const stale = problem !== null && problem.isStale;
  const reload = () => {
    approve.clearProblem();
    cancel.clearProblem();
    onReload();
  };

  const meta: MetaItem[] = [
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
    { label: 'Sahip', value: personLabel(order.ownerDisplayName, order.ownerUserId, me.id) },
    { label: 'Oluşturma', value: <span className="date">{formatDateTime(order.createdAt)}</span> },
  ];
  if (order.approvedAt !== undefined) {
    meta.push({ label: 'Onay tarihi', value: <span className="date">{formatDateTime(order.approvedAt)}</span> });
  }

  if (order.closedAt !== undefined) {
    meta.push({ label: 'Kapanış', value: <span className="date">{formatDateTime(order.closedAt)}</span> });
  }

  if (order.total !== undefined) {
    meta.push({ label: 'Toplam', value: <span className="num">{formatMoney(order.total)}</span> });
  }

  if (order.note !== undefined) {
    meta.push({ label: 'Not', value: <span className="pretext">{order.note}</span> });
  }

  if (order.cancelReason !== undefined) {
    meta.push({ label: 'İptal gerekçesi', value: <span className="pretext">{order.cancelReason}</span> });
  }

  const lineColumns: Column<PurchaseLine>[] = [
    { id: 'sku', header: 'SKU', kind: 'code', rowHeader: true, cell: (l) => <span translate="no">{l.sku}</span> },
    { id: 'name', header: 'Ürün', kind: 'name', cell: (l) => l.productName },
    { id: 'unit', header: 'Birim', cell: (l) => l.unit },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (l) => <Qty value={l.orderedQty} /> },
    { id: 'received', header: 'Kabul edilen', kind: 'num', cell: (l) => <Qty value={l.receivedQty} /> },
    { id: 'cancelled', header: 'İptal', kind: 'num', cell: (l) => <Qty value={l.cancelledQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (l) => <Qty value={l.openQty} /> },
  ];
  // Warehouse operators get the fulfilment view: cost columns are absent, not blanked.
  if (full) {
    lineColumns.push(
      { id: 'cost', header: 'Birim maliyet', kind: 'num', cell: (l) => (l.unitCost !== undefined ? formatMoney(l.unitCost) : '—') },
      { id: 'total', header: 'Tutar', kind: 'num', cell: (l) => (l.unitCost !== undefined ? formatMoney(l.unitCost * l.orderedQty) : '—') },
    );
  }

  const receiptColumns: Column<ReceiptSummary>[] = [
    { id: 'number', header: 'Kabul no', kind: 'code', rowHeader: true, cell: (r) => <span translate="no">{r.number}</span> },
    { id: 'reference', header: 'Tedarikçi irsaliyesi', kind: 'code', cell: (r) => <span translate="no">{r.externalReference}</span> },
    { id: 'date', header: 'Tarih', kind: 'date', cell: (r) => formatDateTime(r.createdAt) },
    { id: 'quantity', header: 'Miktar', kind: 'num', cell: (r) => <Qty value={r.totalQuantity} /> },
  ];

  const stoppedAt: PurchaseOrderStatus | undefined =
    order.status === 'Cancelled' ? (received > 0 ? 'PartiallyReceived' : order.approvedAt !== undefined ? 'Approved' : 'Draft') : undefined;

  return (
    <>
      <PageHead
        title="Satın alma siparişi"
        plate={order.number}
        sub={
          <>
            <StatusTag
              look={lookOf(PURCHASE_STATUS, order.status)}
              note={order.status === 'Cancelled' && received > 0 ? `${formatQuantity(received)}/${formatQuantity(ordered)} kabul edildi` : undefined}
            />
            {!full && <span className="muted">Depo görünümü: maliyet ve ticari notlar gösterilmez.</span>}
          </>
        }
        lead={nextStep(order, open, received)}
        meta={<Meta items={meta} />}
      />

      <div>
        <FlowSteps label="Satın alma akışı" steps={linearSteps(FLOW, PURCHASE_STATUS, order.status, stoppedAt)} />
        {order.status === 'Cancelled' && (
          <p className="small">Sonuç: iptal edildi{order.closedAt !== undefined ? ` (${formatDateTime(order.closedAt)})` : ''}.</p>
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
      {problem !== null && !stale && (
        <ProblemAlert
          problem={problem}
          title={approve.problem !== null ? 'Satın alma siparişi onaylanamadı' : 'Satın alma siparişi iptal edilemedi'}
          onRetry={problem.code === 'invalid_state' ? reload : undefined}
          retryLabel="Güncel siparişi yükle"
          retryPending={reloading}
          hideFieldErrors={cancel.problem !== null && cancel.problem.errors.some((e) => e.field === 'reason')}
        >
          {problem.code === 'approval_limit_exceeded' && (
            <p>
              Onay limitiniz {formatMoney(me.poApprovalLimit)}
              {order.total !== undefined && <>, sipariş toplamı {formatMoney(order.total)}</>}. Sipariş taslak olarak duruyor; hiçbir şey
              değişmedi.
            </p>
          )}
        </ProblemAlert>
      )}

      {canApprove && (
        <InfoNote title="Onay limiti" tone={overLimit ? 'warn' : 'info'}>
          <p>
            Onay limitiniz <strong className="num">{formatMoney(me.poApprovalLimit)}</strong>
            {order.total !== undefined && (
              <>
                ; bu siparişin toplamı <strong className="num">{formatMoney(order.total)}</strong>
              </>
            )}
            .{' '}
            {overLimit
              ? 'Toplam limitinizi aşıyor: onay isteğiniz reddedilir. Limiti yeterli bir satın alma uzmanı onaylayabilir.'
              : 'Toplam limitinizin içinde.'}
          </p>
        </InfoNote>
      )}

      <section className="region balance" aria-labelledby="po-denge">
        <h2 className="sr-only" id="po-denge">
          Miktar dengesi
        </h2>
        <BalanceStrip model={purchaseBalance(order.lines)} />
      </section>

      <section className="region tablewrap">
        <DataTable
          caption="Satırlar"
          captionNote={`${order.lines.length} satır`}
          columns={lineColumns}
          rows={order.lines}
          rowKey={(l) => l.id}
          footer={{
            label: 'Toplam',
            span: 3,
            cells: {
              ordered: <Qty value={ordered} />,
              received: <Qty value={received} />,
              cancelled: <Qty value={sum(order.lines, (l) => l.cancelledQty)} />,
              open: <Qty value={open} />,
              total: order.total !== undefined ? formatMoney(order.total) : undefined,
            },
          }}
        />
      </section>

      <section className="region tablewrap">
        {order.receipts.length > 0 ? (
          <DataTable
            caption="Mal kabuller"
            captionNote={`${order.receipts.length} kabul · toplam ${formatQuantity(received)}`}
            columns={receiptColumns}
            rows={order.receipts}
            rowKey={(r) => r.id}
          />
        ) : (
          <div className="region--pad">
            <h2 className="section-h">Mal kabuller</h2>
            <p className="small">
              {order.status === 'Draft' ? 'Taslak sipariş için mal kabul yapılamaz; önce onaylanmalıdır.' : 'Bu sipariş için henüz mal kabul kaydedilmedi.'}
            </p>
          </div>
        )}
      </section>

      {canCancel && (
        <CancelOrder
          disclosure={cancelStep}
          formId="po-iptal-form"
          openQty={open}
          fulfilledQty={received}
          fulfilledWord="kabul edilen"
          releasesReservation={false}
          pending={cancel.pending}
          serverError={cancel.problem?.errors.find((e) => e.field === 'reason')?.detail}
          onConfirm={(reason) => void cancel.run({ reason, etag: order.eTag })}
        />
      )}

      {(canEdit || canCancel || canApprove || canReceive) && (
        <div className="actions actions--sticky">
          {canEdit && (
            <KLink className="btn" to={`/satin-alma/siparisler/${order.id}/duzenle`}>
              Taslağı düzenle
            </KLink>
          )}
          <span className="spacer" />
          {canCancel && (
            <Button
              variant="danger"
              aria-expanded={cancelStep.open}
              aria-controls={cancelStep.panelId}
              onClick={cancelStep.toggle}
            >
              {received > 0 ? `Kalanı iptal et (${formatQuantity(open)})…` : 'Siparişi iptal et…'}
            </Button>
          )}
          {canApprove && (
            <Button variant="primary" onClick={() => void approve.run(order.eTag)} pending={approve.pending} pendingLabel="Onaylanıyor…">
              Siparişi onayla
            </Button>
          )}
          {canReceive && (
            <KLink className="btn btn--primary" to={`/depo/kabul/${order.id}`}>
              Mal kabul gir
            </KLink>
          )}
        </div>
      )}
      {buyer && order.status === 'Draft' && !mine && <p className="small">Taslağı yalnız sahibi düzenleyebilir.</p>}
    </>
  );
}

export function PurchaseOrderDetailScreen() {
  const { id = '' } = useParams();
  const me = useMe();
  const query = useApiQuery(['purchase-order', id], (signal) => purchasingApi.get(id, signal));
  const back = abilitiesOf(me).purchasing ? (
    <KLink className="btn" to="/satin-alma/siparisler">
      Satın alma siparişlerine dön
    </KLink>
  ) : (
    <KLink className="btn" to="/depo/kabul">
      Kabul bekleyenlere dön
    </KLink>
  );

  return (
    <div className="view">
      <QueryView query={query} loadingLabel="Satın alma siparişi yükleniyor…" what="Sipariş" notFoundAction={back}>
        {(order) => <PurchaseOrderDocument order={order} onReload={() => void query.refetch()} reloading={query.isFetching} />}
      </QueryView>
    </div>
  );
}
