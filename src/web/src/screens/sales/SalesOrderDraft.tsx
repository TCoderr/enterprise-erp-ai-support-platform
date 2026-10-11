import { useParams } from 'react-router-dom';
import { salesApi } from '../../api/endpoints';
import type { SalesOrder } from '../../api/types';
import { EmptyState, QueryView } from '../../components/states';
import { PageHead, StatusTag } from '../../components/ui';
import { SALES_STATUS, lookOf } from '../../lib/labels';
import { useApiQuery, useInvalidate, useSetCached } from '../../lib/query';
import { KLink, useGo } from '../../shell/context';
import { OrderDraftForm } from '../shared/OrderDraftForm';
import type { DraftInitial } from '../shared/OrderDraftForm';

export function SalesOrderNewScreen() {
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  return (
    <div className="view">
      <PageHead
        title="Yeni satış siparişi"
        lead="Sipariş taslak olarak kaydedilir. Taslak stok ayırmaz; siparişi belgesinden onayladığınızda tüm satırlar rezerve edilir."
      />
      <OrderDraftForm
        kind="sales"
        onCancel={() => go('/satis/siparisler')}
        onSaved={(order) => {
          setCached(['sales-order', order.id], order);
          void invalidate('sales-orders');
          go(`/satis/siparisler/${order.id}`, { replace: true });
        }}
      />
    </div>
  );
}

function toInitial(order: SalesOrder): DraftInitial {
  return {
    id: order.id,
    number: order.number,
    partyId: order.customerId,
    partyLabel: `${order.customerCode} · ${order.customerName}`,
    warehouseId: order.warehouseId,
    note: order.note ?? '',
    lines: order.lines.map((line) => ({
      productId: line.productId,
      sku: line.sku,
      name: line.productName,
      unit: line.unit,
      quantity: String(line.orderedQty),
      unitCost: '',
      price: line.unitPrice,
    })),
  };
}

export function SalesOrderEditScreen() {
  const { id = '' } = useParams();
  const go = useGo();
  const invalidate = useInvalidate();
  const setCached = useSetCached();
  const query = useApiQuery(['sales-order', id], (signal) => salesApi.get(id, signal));

  return (
    <div className="view">
      <QueryView
        query={query}
        loadingLabel="Taslak yükleniyor…"
        what="Sipariş"
        notFoundAction={
          <KLink className="btn" to="/satis/siparisler">
            Siparişlere dön
          </KLink>
        }
      >
        {(order) =>
          order.status !== 'Draft' || order.view !== 'Full' ? (
            <>
              <PageHead title="Satış siparişi" plate={order.number} sub={<StatusTag look={lookOf(SALES_STATUS, order.status)} />} />
              <EmptyState
                title="Bu sipariş artık düzenlenemez"
                icon="lock"
                action={
                  <KLink className="btn" to={`/satis/siparisler/${order.id}`}>
                    Sipariş belgesini aç
                  </KLink>
                }
              >
                Yalnız taslak durumundaki siparişin satırları değiştirilebilir. Onaylanmış bir siparişte düzeltme gerekiyorsa kalan miktar iptal
                edilip yeni sipariş açılır.
              </EmptyState>
            </>
          ) : (
            <>
              <PageHead
                title="Taslağı düzenle"
                plate={order.number}
                sub={<StatusTag look={lookOf(SALES_STATUS, order.status)} />}
                lead="Kaydettiğinizde taslağın tüm satırları buradaki satırlarla değiştirilir ve fiyatlar güncel liste fiyatından yeniden alınır."
              />
              <OrderDraftForm
                kind="sales"
                initial={toInitial(order)}
                etag={order.eTag}
                onReload={() => query.refetch()}
                onCancel={() => go(`/satis/siparisler/${order.id}`)}
                onSaved={(saved) => {
                  setCached(['sales-order', saved.id], saved);
                  void invalidate('sales-orders');
                  go(`/satis/siparisler/${saved.id}`, { replace: true });
                }}
              />
            </>
          )
        }
      </QueryView>
    </div>
  );
}
