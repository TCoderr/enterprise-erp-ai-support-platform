import { Component, Suspense, lazy } from 'react';
import type { ComponentType, ReactElement, ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { abilitiesOf, homePathFor } from './auth/permissions';
import { useMe } from './auth/session';
import { Icon } from './components/Icon';
import { EmptyState, ForbiddenState, LoadingRegion } from './components/states';
import { Button, PageHead } from './components/ui';
import { ChangePasswordScreen } from './screens/auth/PasswordScreen';
import { KLink, useWarehouse } from './shell/context';

// Each work area is its own chunk: a warehouse operator never downloads the admin screens.
function screen<M>(load: () => Promise<M>, pick: (module: M) => ComponentType) {
  return lazy(async () => ({ default: pick(await load()) }));
}

const sales = () => import('./screens/sales');
const purchasing = () => import('./screens/purchasing');
const inventory = () => import('./screens/inventory');
const support = () => import('./screens/support');
const kb = () => import('./screens/kb');
const admin = () => import('./screens/admin');

const SalesOrderListScreen = screen(sales, (m) => m.SalesOrderListScreen);
const SalesOrderNewScreen = screen(sales, (m) => m.SalesOrderNewScreen);
const SalesOrderEditScreen = screen(sales, (m) => m.SalesOrderEditScreen);
const SalesOrderDetailScreen = screen(sales, (m) => m.SalesOrderDetailScreen);
const SalesOrderSummaryScreen = screen(sales, (m) => m.SalesOrderSummaryScreen);

const PurchaseOrderListScreen = screen(purchasing, (m) => m.PurchaseOrderListScreen);
const PurchaseOrderNewScreen = screen(purchasing, (m) => m.PurchaseOrderNewScreen);
const PurchaseOrderEditScreen = screen(purchasing, (m) => m.PurchaseOrderEditScreen);
const PurchaseOrderDetailScreen = screen(purchasing, (m) => m.PurchaseOrderDetailScreen);

const BalancesScreen = screen(inventory, (m) => m.BalancesScreen);
const MovementsScreen = screen(inventory, (m) => m.MovementsScreen);
const ReservationsScreen = screen(inventory, (m) => m.ReservationsScreen);
const DispatchQueueScreen = screen(inventory, (m) => m.DispatchQueueScreen);
const DispatchScreen = screen(inventory, (m) => m.DispatchScreen);
const ReceiveQueueScreen = screen(inventory, (m) => m.ReceiveQueueScreen);
const ReceiveScreen = screen(inventory, (m) => m.ReceiveScreen);
const TransferScreen = screen(inventory, (m) => m.TransferScreen);
const AdjustmentScreen = screen(inventory, (m) => m.AdjustmentScreen);

const MyTicketsScreen = screen(support, (m) => m.MyTicketsScreen);
const TicketQueueScreen = screen(support, (m) => m.TicketQueueScreen);
const TicketNewScreen = screen(support, (m) => m.TicketNewScreen);
const TicketDetailScreen = screen(support, (m) => m.TicketDetailScreen);

const ArticleListScreen = screen(kb, (m) => m.ArticleListScreen);
const ArticleScreen = screen(kb, (m) => m.ArticleScreen);
const KbManageListScreen = screen(kb, (m) => m.KbManageListScreen);
const KbManageNewScreen = screen(kb, (m) => m.KbManageNewScreen);
const KbManageDetailScreen = screen(kb, (m) => m.KbManageDetailScreen);

const UsersScreen = screen(admin, (m) => m.UsersScreen);
const UserDetailScreen = screen(admin, (m) => m.UserDetailScreen);
const MasterDataScreen = screen(admin, (m) => m.MasterDataScreen);
const AuditScreen = screen(admin, (m) => m.AuditScreen);
const OperationsScreen = screen(admin, (m) => m.OperationsScreen);

function HomeLink() {
  return (
    <KLink className="btn" to="/">
      Açılış sayfasına dön
    </KLink>
  );
}

/** The role does not include this screen. Hiding it is presentation; the server enforces the rule. */
function NoAccessScreen() {
  return (
    <div className="view">
      <PageHead title="Bu ekran rolünüze açık değil" />
      <ForbiddenState action={<HomeLink />} />
    </div>
  );
}

function NotFoundScreen() {
  return (
    <div className="view">
      <PageHead title="Sayfa bulunamadı" />
      <EmptyState title="Bu adreste bir sayfa yok" icon="lock" action={<HomeLink />}>
        Adresi kontrol edin ya da soldaki gezinmeden bir ekran seçin.
      </EmptyState>
    </div>
  );
}

function Home() {
  const me = useMe();
  const { href } = useWarehouse();
  return <Navigate to={href(homePathFor(me))} replace />;
}

interface BoundaryProps {
  /** Changes with the route, so a failed screen does not stick after navigating away. */
  resetKey: string;
  children: ReactNode;
}

interface BoundaryState {
  failed: boolean;
}

/** A screen that fails to load or render shows a recoverable message instead of a blank page. */
class ScreenBoundary extends Component<BoundaryProps, BoundaryState> {
  override state: BoundaryState = { failed: false };

  static getDerivedStateFromError(): BoundaryState {
    return { failed: true };
  }

  override componentDidUpdate(previous: BoundaryProps): void {
    if (this.state.failed && previous.resetKey !== this.props.resetKey) {
      this.setState({ failed: false });
    }
  }

  override render(): ReactNode {
    if (this.state.failed) {
      return (
        <div className="view">
          <div className="alert alert--danger" role="alert">
            <Icon name="error" />
            <h1>Bu ekran açılamadı</h1>
            <p>
              Ekranın dosyaları yüklenemedi ya da beklenmeyen bir istemci hatası oluştu. Kaydedilmemiş bir işleminiz varsa sunucuya ulaşmamıştır.
              Sayfayı yenileyin; sorun sürerse destek ekibine haber verin.
            </p>
            <div className="actions">
              <Button onClick={() => window.location.reload()}>Sayfayı yenile</Button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

export function AppRoutes() {
  const can = abilitiesOf(useMe());
  const location = useLocation();
  const guard = (allowed: boolean, element: ReactElement): ReactElement => (allowed ? element : <NoAccessScreen />);
  const ordersVisible = can.sales || can.warehouseOps;
  const purchaseVisible = can.purchasing || can.warehouseOps;

  return (
    <ScreenBoundary resetKey={location.pathname}>
      <Suspense
        fallback={
          <div className="view">
            <LoadingRegion label="Ekran yükleniyor…" />
          </div>
        }
      >
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/parola" element={<ChangePasswordScreen />} />

          <Route path="/satis/siparisler" element={guard(can.sales, <SalesOrderListScreen />)} />
          <Route path="/satis/siparisler/yeni" element={guard(can.sales, <SalesOrderNewScreen />)} />
          <Route path="/satis/siparisler/:id" element={guard(ordersVisible, <SalesOrderDetailScreen />)} />
          <Route path="/satis/siparisler/:id/duzenle" element={guard(can.sales, <SalesOrderEditScreen />)} />
          <Route path="/satis/siparisler/:id/ozet" element={guard(ordersVisible, <SalesOrderSummaryScreen />)} />

          <Route path="/satin-alma/siparisler" element={guard(can.purchasing, <PurchaseOrderListScreen />)} />
          <Route path="/satin-alma/siparisler/yeni" element={guard(can.purchasing, <PurchaseOrderNewScreen />)} />
          <Route path="/satin-alma/siparisler/:id" element={guard(purchaseVisible, <PurchaseOrderDetailScreen />)} />
          <Route path="/satin-alma/siparisler/:id/duzenle" element={guard(can.purchasing, <PurchaseOrderEditScreen />)} />

          <Route path="/depo/stok" element={guard(can.stock, <BalancesScreen />)} />
          <Route path="/depo/hareketler" element={guard(can.stock, <MovementsScreen />)} />
          <Route path="/depo/rezervasyonlar" element={guard(can.stock, <ReservationsScreen />)} />
          <Route path="/depo/sevk" element={guard(can.warehouseOps, <DispatchQueueScreen />)} />
          <Route path="/depo/sevk/:id" element={guard(can.warehouseOps, <DispatchScreen />)} />
          <Route path="/depo/kabul" element={guard(can.warehouseOps, <ReceiveQueueScreen />)} />
          <Route path="/depo/kabul/:id" element={guard(can.warehouseOps, <ReceiveScreen />)} />
          <Route path="/depo/transfer" element={guard(can.transfer, <TransferScreen />)} />
          <Route path="/depo/duzeltme" element={guard(can.adjust, <AdjustmentScreen />)} />

          <Route path="/destek/talepler" element={<MyTicketsScreen />} />
          <Route path="/destek/talepler/yeni" element={<TicketNewScreen />} />
          <Route path="/destek/talepler/:id" element={<TicketDetailScreen />} />
          <Route path="/destek/kuyruk" element={guard(can.supportStaff, <TicketQueueScreen />)} />

          <Route path="/bilgi-bankasi" element={<ArticleListScreen />} />
          <Route path="/bilgi-bankasi/makale/:slug" element={<ArticleScreen />} />
          <Route path="/bilgi-bankasi/yonetim" element={guard(can.supportStaff, <KbManageListScreen />)} />
          <Route path="/bilgi-bankasi/yonetim/yeni" element={guard(can.supportStaff, <KbManageNewScreen />)} />
          <Route path="/bilgi-bankasi/yonetim/:id" element={guard(can.supportStaff, <KbManageDetailScreen />)} />

          <Route path="/yonetim/kullanicilar" element={guard(can.admin, <UsersScreen />)} />
          <Route path="/yonetim/kullanicilar/:id" element={guard(can.admin, <UserDetailScreen />)} />
          <Route path="/yonetim/ana-veri" element={guard(can.admin, <MasterDataScreen />)} />
          <Route path="/yonetim/denetim" element={guard(can.admin, <AuditScreen />)} />
          <Route path="/yonetim/operasyon" element={guard(can.admin, <OperationsScreen />)} />

          <Route path="*" element={<NotFoundScreen />} />
        </Routes>
      </Suspense>
    </ScreenBoundary>
  );
}
