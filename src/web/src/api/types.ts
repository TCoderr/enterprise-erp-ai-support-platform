// Wire types of the Kantar API. The server serialises with System.Text.Json camelCase, writes enums as
// strings and OMITS null properties, so every nullable member is optional here.

export type Role =
  | 'SalesAgent'
  | 'SalesManager'
  | 'PurchasingAgent'
  | 'WarehouseOperator'
  | 'SupportAgent'
  | 'SupportManager'
  | 'SystemAdmin';

export const ALL_ROLES: readonly Role[] = [
  'SalesAgent',
  'SalesManager',
  'PurchasingAgent',
  'WarehouseOperator',
  'SupportAgent',
  'SupportManager',
  'SystemAdmin',
];

export type WarehouseLevel = 'View' | 'Operate' | 'Supervise';

export interface Page<T> {
  items: T[];
  nextCursor?: string;
}

// ---- identity ----
export interface WarehouseAccess {
  id: string;
  code: string;
  name: string;
  level: WarehouseLevel;
}

export interface Me {
  id: string;
  userName: string;
  displayName: string;
  roles: Role[];
  warehouses: WarehouseAccess[];
  mustChangePassword: boolean;
  poApprovalLimit: number;
}

export interface LoginResponse {
  user: Me;
  csrfToken: string;
}

export interface Grant {
  warehouseId: string;
  level: WarehouseLevel;
}

export interface AdminUser {
  id: string;
  userName: string;
  displayName: string;
  isActive: boolean;
  mustChangePassword: boolean;
  poApprovalLimit: number;
  roles: Role[];
  grants: Grant[];
  eTag: string;
}

export interface TemporaryPassword {
  user: AdminUser;
  temporaryPassword: string;
}

export interface AuditEntry {
  id: number;
  occurredAt: string;
  actorUserId: string;
  action: string;
  entityType: string;
  entityId: string;
  warehouseId?: string;
  correlationId: string;
}

// ---- master data ----
export interface Product {
  id: string;
  sku: string;
  name: string;
  unit: string;
  /** Present only for roles that may read prices. */
  listPrice?: number;
  reorderLevel: number;
  isActive: boolean;
  eTag: string;
}

export interface Customer {
  id: string;
  code: string;
  name: string;
  taxNumber: string;
  address: string;
  isActive: boolean;
  eTag: string;
}

export interface Supplier {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  eTag: string;
}

export interface Warehouse {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  eTag: string;
}

export const PRODUCT_UNITS = ['ADET', 'KOLI', 'PAKET', 'METRE', 'KG', 'TOP'] as const;

// ---- inventory ----
export interface Balance {
  productId: string;
  sku: string;
  name: string;
  unit: string;
  onHand: number;
  reserved: number;
  available: number;
  reorderLevel: number;
  isLow: boolean;
}

export type MovementType = 'Receipt' | 'Shipment' | 'TransferOut' | 'TransferIn' | 'Adjustment';

export interface Movement {
  id: number;
  occurredAt: string;
  productId: string;
  sku: string;
  name: string;
  type: MovementType;
  quantityDelta: number;
  onHandAfter: number;
  referenceType: 'GoodsReceipt' | 'Shipment' | 'StockTransfer' | 'StockAdjustment';
  referenceId: string;
  referenceNumber?: string;
}

export type ReservationReason = 'Reserve' | 'ReleaseOnShipment' | 'ReleaseOnCancel';

export interface Reservation {
  id: number;
  occurredAt: string;
  productId: string;
  sku: string;
  name: string;
  reason: ReservationReason;
  quantityDelta: number;
  reservedAfter: number;
  salesOrderId: string;
  salesOrderNumber?: string;
}

export interface TransferLine {
  lineNo: number;
  productId: string;
  sku: string;
  name: string;
  unit: string;
  quantity: number;
}

export interface Transfer {
  id: string;
  number: string;
  reference: string;
  fromWarehouseId: string;
  toWarehouseId: string;
  note?: string;
  createdBy: string;
  createdByDisplayName: string;
  createdAt: string;
  lines: TransferLine[];
}

export type AdjustmentReason = 'CountDifference' | 'Damage' | 'Loss' | 'Found' | 'DataCorrection';

export interface AdjustmentLine {
  lineNo: number;
  productId: string;
  sku: string;
  name: string;
  unit: string;
  quantityDelta: number;
}

export interface Adjustment {
  id: string;
  number: string;
  reference: string;
  warehouseId: string;
  reasonCode: AdjustmentReason;
  note: string;
  createdBy: string;
  createdByDisplayName: string;
  createdAt: string;
  lines: AdjustmentLine[];
}

// ---- sales ----
export type SalesOrderStatus = 'Draft' | 'Confirmed' | 'PartiallyShipped' | 'Shipped' | 'Cancelled';

/** Full: commercial view. Fulfilment: quantities only (no prices, totals or notes). */
export type DocumentView = 'Full' | 'Fulfilment';

export interface SalesLine {
  id: string;
  lineNo: number;
  productId: string;
  sku: string;
  productName: string;
  unit: string;
  unitPrice?: number;
  lineTotal?: number;
  orderedQty: number;
  shippedQty: number;
  cancelledQty: number;
  openQty: number;
}

export interface ShipmentSummary {
  id: string;
  number: string;
  dispatchReference: string;
  createdAt: string;
  totalQuantity: number;
}

export interface SalesOrder {
  id: string;
  number: string;
  status: SalesOrderStatus;
  view: DocumentView;
  customerId: string;
  customerCode: string;
  customerName: string;
  customerTaxNumber?: string;
  shippingAddress: string;
  warehouseId: string;
  ownerUserId: string;
  /** "—" when the user no longer exists. */
  ownerDisplayName: string;
  note?: string;
  total?: number;
  createdAt: string;
  confirmedAt?: string;
  closedAt?: string;
  cancelReason?: string;
  lines: SalesLine[];
  shipments: ShipmentSummary[];
  eTag: string;
}

export interface SalesOrderListItem {
  id: string;
  number: string;
  status: SalesOrderStatus;
  customerName: string;
  warehouseId: string;
  ownerUserId: string;
  ownerDisplayName: string;
  createdAt: string;
  total?: number;
  orderedQty: number;
  shippedQty: number;
  openQty: number;
}

export interface FulfilmentDocLine {
  orderLineId: string;
  productId: string;
  sku: string;
  productName: string;
  unit: string;
  quantity: number;
}

export interface Shipment {
  id: string;
  number: string;
  salesOrderId: string;
  salesOrderNumber: string;
  warehouseId: string;
  dispatchReference: string;
  createdBy: string;
  createdByDisplayName: string;
  createdAt: string;
  lines: FulfilmentDocLine[];
  salesOrderStatus: SalesOrderStatus;
  salesOrderETag: string;
}

export interface SalesOrderSummary {
  order: SalesOrder;
  warehouseCode: string;
  warehouseName: string;
  generatedAt: string;
  legalNotice: string;
}

// ---- purchasing ----
export type PurchaseOrderStatus = 'Draft' | 'Approved' | 'PartiallyReceived' | 'Received' | 'Cancelled';

export interface PurchaseLine {
  id: string;
  lineNo: number;
  productId: string;
  sku: string;
  productName: string;
  unit: string;
  unitCost?: number;
  orderedQty: number;
  receivedQty: number;
  cancelledQty: number;
  openQty: number;
}

export interface ReceiptSummary {
  id: string;
  number: string;
  externalReference: string;
  createdAt: string;
  totalQuantity: number;
}

export interface PurchaseOrder {
  id: string;
  number: string;
  status: PurchaseOrderStatus;
  view: DocumentView;
  supplierId: string;
  supplierCode: string;
  supplierName: string;
  warehouseId: string;
  ownerUserId: string;
  /** "—" when the user no longer exists. */
  ownerDisplayName: string;
  note?: string;
  total?: number;
  createdAt: string;
  approvedAt?: string;
  closedAt?: string;
  cancelReason?: string;
  lines: PurchaseLine[];
  receipts: ReceiptSummary[];
  eTag: string;
}

export interface PurchaseOrderListItem {
  id: string;
  number: string;
  status: PurchaseOrderStatus;
  supplierName: string;
  warehouseId: string;
  ownerUserId: string;
  ownerDisplayName: string;
  createdAt: string;
  total?: number;
  orderedQty: number;
  receivedQty: number;
  openQty: number;
}

export interface Receipt {
  id: string;
  number: string;
  purchaseOrderId: string;
  purchaseOrderNumber: string;
  warehouseId: string;
  externalReference: string;
  createdBy: string;
  createdByDisplayName: string;
  createdAt: string;
  lines: FulfilmentDocLine[];
  purchaseOrderStatus: PurchaseOrderStatus;
  purchaseOrderETag: string;
}

// ---- support ----
export type TicketStatus = 'Open' | 'InProgress' | 'WaitingForRequester' | 'Resolved' | 'Closed';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type AppModule = 'General' | 'Sales' | 'Purchasing' | 'Inventory' | 'Support';
export type RelatedDocumentType =
  | 'SalesOrder'
  | 'PurchaseOrder'
  | 'Shipment'
  | 'GoodsReceipt'
  | 'StockTransfer'
  | 'StockAdjustment';

export interface Citation {
  documentSlug: string;
  title: string;
  versionNo: number;
  sectionNo: number;
  heading: string;
}

export interface TicketComment {
  id: string;
  authorUserId: string;
  authorDisplayName: string;
  body: string;
  isInternal: boolean;
  source: 'User' | 'AiSuggestion';
  aiProvider?: string;
  aiModel?: string;
  citations?: Citation[];
  createdAt: string;
}

export interface TicketDiagnostic {
  appVersion: string;
  module: string;
  screen?: string;
  errorCode?: string;
  correlationId: string;
  requesterRoles: string[];
  serverTimeUtc: string;
}

export interface Ticket {
  id: string;
  number: string;
  status: TicketStatus;
  priority: TicketPriority;
  module: AppModule;
  title: string;
  description: string;
  screen?: string;
  errorCode?: string;
  relatedDocumentType?: RelatedDocumentType;
  relatedDocumentNumber?: string;
  requesterUserId: string;
  requesterDisplayName: string;
  assigneeUserId?: string;
  assigneeDisplayName?: string;
  createdAt: string;
  updatedAt: string;
  diagnostic?: TicketDiagnostic;
  comments: TicketComment[];
  eTag: string;
}

export interface TicketListItem {
  id: string;
  number: string;
  status: TicketStatus;
  priority: TicketPriority;
  module: AppModule;
  title: string;
  errorCode?: string;
  requesterUserId: string;
  assigneeUserId?: string;
  createdAt: string;
  updatedAt: string;
}

export interface SupportStaff {
  id: string;
  displayName: string;
  /** Only SupportAgent / SupportManager. */
  roles: Role[];
}

/** Screen codes accepted by the server (Ticket.KnownScreens in the domain). */
export const KNOWN_SCREENS = [
  'sales.orders',
  'sales.order',
  'sales.dispatch',
  'purchasing.orders',
  'purchasing.order',
  'purchasing.receive',
  'inventory.balances',
  'inventory.movements',
  'inventory.transfer',
  'inventory.adjustment',
  'support.tickets',
  'support.ticket',
  'kb.articles',
  'kb.manage',
  'assist.panel',
  'admin.users',
  'auth.login',
] as const;

// ---- knowledge base ----
export type KbVisibility = 'General' | 'SupportOnly' | 'ManagerOnly';
export type KbStatus = 'Draft' | 'Published' | 'Retired';

export interface KbArticleListItem {
  slug: string;
  title: string;
  module: AppModule;
  visibility: KbVisibility;
  errorCode?: string;
  warehouseId?: string;
  versionNo: number;
  updatedAt: string;
}

export interface KbArticle {
  slug: string;
  title: string;
  bodyMarkdown: string;
  module: AppModule;
  visibility: KbVisibility;
  errorCode?: string;
  warehouseId?: string;
  versionNo: number;
  publishedVersionCreatedAt: string;
}

export interface KbVersion {
  versionNo: number;
  title: string;
  bodyMarkdown: string;
  authorUserId: string;
  createdAt: string;
  isPublished: boolean;
}

export interface KbDocument {
  id: string;
  slug: string;
  status: KbStatus;
  visibility: KbVisibility;
  module: AppModule;
  warehouseId?: string;
  errorCode?: string;
  publishedVersionNo?: number;
  latestVersionNo: number;
  updatedAt: string;
  versions: KbVersion[];
  eTag: string;
}

// ---- assistance ----
export interface ProviderInfo {
  kind: 'demo' | 'local-model' | 'disabled' | (string & {});
  model?: string;
  isDemo: boolean;
}

export interface AssistSource extends Citation {
  snippet: string;
}

export type AnswerStatus = 'Answered' | 'NoSource' | 'ProviderUnavailable' | 'RejectedUnsupported' | 'AccessChanged';

export interface AssistAnswer {
  id?: string;
  status: AnswerStatus;
  answer?: string;
  citations: Citation[];
  sources: AssistSource[];
  limitations?: string;
  provider: ProviderInfo;
  expiresAt?: string;
}

// ---- operations ----
export interface OutboxSummary {
  pending: number;
  leased: number;
  retrying: number;
  dead: number;
  processedLast24Hours: number;
  oldestPendingCreatedAt?: string;
}

export interface ReconciliationFinding {
  warehouseId: string;
  warehouseCode: string;
  productId: string;
  sku: string;
  rule: string;
  expected: number;
  actual: number;
}

export interface ReconciliationRun {
  id: string;
  startedAt: string;
  completedAt: string;
  balancesChecked: number;
  findingCount: number;
  outcome: 'Consistent' | 'DriftFound' | (string & {});
  findings: ReconciliationFinding[];
}

export interface MetricRow {
  name: string;
  dimension1: string;
  dimension2: string;
  count: number;
}

export interface AiUsageRow {
  provider: string;
  status: string;
  count: number;
  averageDurationMs: number;
}

export interface Metrics {
  processStartedAt: string;
  counters: MetricRow[];
  aiUsageLast24Hours: AiUsageRow[];
}

export interface AppNotification {
  id: string;
  kind: string;
  text: string;
  createdAt: string;
  isRead: boolean;
}
