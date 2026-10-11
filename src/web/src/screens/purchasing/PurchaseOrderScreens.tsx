import { useParams } from 'react-router-dom';
import { purchasingApi } from '../../api/endpoints';
import type { PurchaseOrder, PurchaseOrderListItem, PurchaseOrderStatus } from '../../api/types';
import { useMe } from '../../auth/session';
import { DataTable } from '../../components/DataTable';
import type { Column } from '../../components/DataTable';
import { CheckField, SelectField } from '../../components/forms';
import { EmptyState, QueryView } from '../../components/states';
import { Button, Code, PageHead, Qty, StatusTag, Tag } from '../../components/ui';
import { formatDateTime, formatDecimal, formatMoney } from '../../lib/format';
import { PURCHASE_STATUS, lookOf, personLabel } from '../../lib/labels';
import { ListRegion, useCursorList, useUrlState } from '../../lib/lists';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { KLink, useGo, useWarehouse } from '../../shell/context';
import { OrderDraftForm } from '../shared/OrderDraftForm';
import type { DraftInitial } from '../shared/OrderDraftForm';

const STATUSES: readonly PurchaseOrderStatus[] = ['Draft', 'Approved', 'PartiallyReceived', 'Received', 'Cancelled'];
const FORM_ID = 'po-suzgec';

export function PurchaseOrderListScreen() {
  const me = useMe();
  const { selected } = useWarehouse();
  const { params, update } = useUrlState();
  const statusParam = params.get('durum');
  const status = STATUSES.find((s) => s === statusParam);
  const mine = params.get('benim') === '1';
  const filtered = status !== undefined || mine;

  const list = useCursorList<PurchaseOrderListItem>(['purchase-orders', selected?.id ?? null, status ?? null, mine], (cursor, signal) =>
    purchasingApi.list({ warehouseId: selected?.id, status, mine, cursor }, signal),
  );

  const showTotals = list.items.some((item) => item.total !== undefined);
  const columns: Column<PurchaseOrderListItem>[] = [
    {
      id: 'number',
      header: 'Sipariş no',
      kind: 'code',
      rowHeader: true,
      cell: (o) => (
        <KLink to={`/satin-alma/siparisler/${o.id}`} translate="no">
          {o.number}
        </KLink>
      ),
    },
    { id: 'supplier', header: 'Tedarikçi', kind: 'name', cell: (o) => o.supplierName },
    { id: 'status', header: 'Durum', cell: (o) => <StatusTag look={lookOf(PURCHASE_STATUS, o.status)} /> },
    { id: 'owner', header: 'Sahip', cell: (o) => personLabel(o.ownerDisplayName, o.ownerUserId, me.id) },
    { id: 'date', header: 'Oluşturma', kind: 'date', cell: (o) => formatDateTime(o.createdAt) },
    { id: 'ordered', header: 'Sipariş', kind: 'num', cell: (o) => <Qty value={o.orderedQty} /> },
    { id: 'received', header: 'Kabul edilen', kind: 'num', cell: (o) => <Qty value={o.receivedQty} /> },
    { id: 'open', header: 'Açık', kind: 'num', cell: (o) => <Qty value={o.openQty} /> },
  ];
  if (showTotals) {
    columns.push({ id: 'total', header: 'Toplam', kind: 'num', cell: (o) => (o.total !== undefined ? formatMoney(o.total) : '—') });
  }

  const clear = () => update({ durum: null, benim: null });
  return (
    <div className="view">
      <PageHead
        title="Satın alma siparişleri"
        sub={
          selected !== null ? (
            <Tag tone="plain">
              <Code>{selected.code}</Code>
              {selected.name}
            </Tag>
          ) : undefined
        }
        actions={
          <KLink className="btn btn--primary" to="/satin-alma/siparisler/yeni">
            Yeni satın alma siparişi
          </KLink>
        }
      />

      <form className="region filters" role="search" aria-label="Satın alma süzgeçleri" onSubmit={(event) => event.preventDefault()}>
        <SelectField
          formId={FORM_ID}
          name="durum"
          label="Durum"
          value={status ?? ''}
          onChange={(value) => update({ durum: value })}
          placeholder="Tüm durumlar"
          options={STATUSES.map((s) => ({ value: s, label: PURCHASE_STATUS[s].label }))}
        />
        <CheckField formId={FORM_ID} name="benim" label="Yalnız benim siparişlerim" checked={mine} onChange={(checked) => update({ benim: checked ? '1' : null })} />
        {filtered && (
          <div>
            <Button onClick={clear}>Süzgeçleri temizle</Button>
          </div>
        )}
      </form>

      <ListRegion
        list={list}
        loadingLabel="Satın alma siparişleri yükleniyor…"
        noun="sipariş"
        scopeNoun="Depo"
        empty={
          filtered ? (
            <EmptyState title="Bu süzgeçle eşleşen satın alma siparişi yok" action={<Button onClick={clear}>Süzgeçleri temizle</Button>}>
              Seçili depoda bu durumda sipariş bulunamadı. Süzgeci değiştirin ya da üstteki depo seçiciyle başka bir depoya bakın.
            </EmptyState>
          ) : (
            <EmptyState
              title="Bu depo için henüz satın alma siparişi yok"
              action={
                <KLink className="btn" to="/satin-alma/siparisler/yeni">
                  İlk satın alma siparişini oluştur
                </KLink>
              }
            >
              Stok yalnız onaylı bir satın alma siparişine yapılan mal kabulle girer; siparişten bağımsız stok girişi yoktur.
            </EmptyState>
          )
        }
      >
        {(items) => <DataTable caption="Satın alma siparişleri" captionHidden columns={columns} rows={items} rowKey={(o) => o.id} />}
      </ListRegion>
    </div>
  );
}

export function PurchaseOrderNewScreen() {
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  return (
    <div className="view">
      <PageHead
        title="Yeni satın alma siparişi"
        lead="Sipariş taslak olarak kaydedilir. Onaylandıktan sonra depo görevlisi teslimatları mal kabul olarak işler; stok o zaman artar."
      />
      <OrderDraftForm
        kind="purchase"
        onCancel={() => go('/satin-alma/siparisler')}
        onSaved={(order) => {
          setCached(['purchase-order', order.id], order);
          void invalidate('purchase-orders');
          go(`/satin-alma/siparisler/${order.id}`, { replace: true });
        }}
      />
    </div>
  );
}

function toInitial(order: PurchaseOrder): DraftInitial {
  return {
    id: order.id,
    number: order.number,
    partyId: order.supplierId,
    partyLabel: `${order.supplierCode} · ${order.supplierName}`,
    warehouseId: order.warehouseId,
    note: order.note ?? '',
    lines: order.lines.map((line) => ({
      productId: line.productId,
      sku: line.sku,
      name: line.productName,
      unit: line.unit,
      quantity: String(line.orderedQty),
      unitCost: line.unitCost !== undefined ? formatDecimal(line.unitCost) : '',
    })),
  };
}

export function PurchaseOrderEditScreen() {
  const { id = '' } = useParams();
  const me = useMe();
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const query = useApiQuery(['purchase-order', id], (signal) => purchasingApi.get(id, signal));

  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Taslak yükleniyor…"
        what="Sipariş"
        notFoundAction={
          <KLink className="btn" to="/satin-alma/siparisler">
            Satın alma siparişlerine dön
          </KLink>
        }
      >
        {(order) =>
          order.status !== 'Draft' || order.view !== 'Full' || order.ownerUserId !== me.id ? (
            <>
              <PageHead title="Satın alma siparişi" plate={order.number} sub={<StatusTag look={lookOf(PURCHASE_STATUS, order.status)} />} />
              <EmptyState
                title="Bu sipariş sizin tarafınızdan düzenlenemez"
                icon="lock"
                action={
                  <KLink className="btn" to={`/satin-alma/siparisler/${order.id}`}>
                    Sipariş belgesini aç
                  </KLink>
                }
              >
                Yalnız taslak durumundaki sipariş, yalnız sahibi tarafından düzenlenebilir.
              </EmptyState>
            </>
          ) : (
            <>
              <PageHead
                title="Taslağı düzenle"
                plate={order.number}
                sub={<StatusTag look={lookOf(PURCHASE_STATUS, order.status)} />}
                lead="Kaydettiğinizde taslağın tüm satırları buradaki satırlarla değiştirilir."
              />
              <OrderDraftForm
                kind="purchase"
                initial={toInitial(order)}
                etag={order.eTag}
                onReload={() => query.refetch()}
                onCancel={() => go(`/satin-alma/siparisler/${order.id}`)}
                onSaved={(saved) => {
                  setCached(['purchase-order', saved.id], saved);
                  void invalidate('purchase-orders');
                  go(`/satin-alma/siparisler/${saved.id}`, { replace: true });
                }}
              />
            </>
          )
        }
      </QueryView>
    </div>
  );
}
