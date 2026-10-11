import { getJson, request } from './http';
import type {
  Adjustment,
  AdjustmentReason,
  AdminUser,
  AppModule,
  AppNotification,
  AssistAnswer,
  AssistSource,
  AuditEntry,
  Balance,
  Customer,
  Grant,
  KbArticle,
  KbArticleListItem,
  KbDocument,
  KbVisibility,
  LoginResponse,
  Me,
  Metrics,
  Movement,
  MovementType,
  OutboxSummary,
  Page,
  Product,
  ProviderInfo,
  PurchaseOrder,
  PurchaseOrderListItem,
  PurchaseOrderStatus,
  Receipt,
  ReconciliationRun,
  RelatedDocumentType,
  Reservation,
  Role,
  SalesOrder,
  SalesOrderListItem,
  SalesOrderStatus,
  SalesOrderSummary,
  Shipment,
  Supplier,
  SupportStaff,
  TemporaryPassword,
  Ticket,
  TicketListItem,
  TicketPriority,
  TicketStatus,
  Transfer,
  Warehouse,
} from './types';

// Conventions used below:
//  - `key` is the Idempotency-Key of the user's intent (required by the server for those commands).
//  - `etag` is the ETag of the document as rendered; it travels as If-Match.
//  - Shipments and receipts deliberately take no etag: they are guarded by the remaining quantity.

const id = (value: string): string => encodeURIComponent(value);

async function send<T>(
  method: 'POST' | 'PUT',
  path: string,
  options: { body?: unknown; key?: string | undefined; etag?: string | undefined } = {},
): Promise<T> {
  const result = await request<T>(path, {
    method,
    body: options.body,
    idempotencyKey: options.key,
    ifMatch: options.etag,
  });
  return result.data;
}

/** Drops undefined members: the API rejects unknown fields and treats a missing optional as null. */
function compact<T extends Record<string, unknown>>(value: T): T {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(value)) {
    if (v !== undefined) {
      out[k] = v;
    }
  }

  return out as T;
}

// ---------------------------------------------------------------- auth
export const authApi = {
  me: async (signal?: AbortSignal): Promise<Me> => (await request<Me>('/api/auth/me', { signal, quiet401: true })).data,
  login: async (userName: string, password: string): Promise<LoginResponse> =>
    (await request<LoginResponse>('/api/auth/login', { method: 'POST', body: { userName, password }, quiet401: true })).data,
  logout: (): Promise<void> => send<void>('POST', '/api/auth/logout'),
  changePassword: (currentPassword: string, newPassword: string): Promise<void> =>
    send<void>('POST', '/api/auth/change-password', { body: { currentPassword, newPassword } }),
};

// ---------------------------------------------------------------- master data
export interface ProductInput {
  sku: string;
  name: string;
  unit: string;
  listPrice: number;
  reorderLevel: number;
}

export interface CustomerInput {
  code: string;
  name: string;
  taxNumber: string;
  address: string;
}

export interface CodeNameInput {
  code: string;
  name: string;
}

export const masterApi = {
  products: (q: { search?: string | undefined; active?: boolean | undefined; cursor?: string | undefined; limit?: number | undefined }, signal?: AbortSignal) =>
    getJson<Page<Product>>('/api/products', { search: q.search, active: q.active, cursor: q.cursor, limit: q.limit }, signal),
  createProduct: (input: ProductInput, key: string) => send<Product>('POST', '/api/products', { body: input, key }),
  updateProduct: (productId: string, input: ProductInput, etag: string) =>
    send<Product>('PUT', `/api/products/${id(productId)}`, { body: input, etag }),
  setProductActive: (productId: string, active: boolean) =>
    send<Product>('POST', `/api/products/${id(productId)}/${active ? 'activate' : 'deactivate'}`),

  customers: (active: boolean | undefined, signal?: AbortSignal) => getJson<Customer[]>('/api/customers', { active }, signal),
  createCustomer: (input: CustomerInput, key: string) => send<Customer>('POST', '/api/customers', { body: input, key }),
  updateCustomer: (customerId: string, input: CustomerInput, etag: string) =>
    send<Customer>('PUT', `/api/customers/${id(customerId)}`, { body: input, etag }),
  setCustomerActive: (customerId: string, active: boolean) =>
    send<Customer>('POST', `/api/customers/${id(customerId)}/${active ? 'activate' : 'deactivate'}`),

  suppliers: (active: boolean | undefined, signal?: AbortSignal) => getJson<Supplier[]>('/api/suppliers', { active }, signal),
  createSupplier: (input: CodeNameInput, key: string) => send<Supplier>('POST', '/api/suppliers', { body: input, key }),
  updateSupplier: (supplierId: string, input: CodeNameInput, etag: string) =>
    send<Supplier>('PUT', `/api/suppliers/${id(supplierId)}`, { body: input, etag }),
  setSupplierActive: (supplierId: string, active: boolean) =>
    send<Supplier>('POST', `/api/suppliers/${id(supplierId)}/${active ? 'activate' : 'deactivate'}`),

  warehouses: (signal?: AbortSignal) => getJson<Warehouse[]>('/api/warehouses', undefined, signal),
  createWarehouse: (input: CodeNameInput, key: string) => send<Warehouse>('POST', '/api/warehouses', { body: input, key }),
  updateWarehouse: (warehouseId: string, input: CodeNameInput, etag: string) =>
    send<Warehouse>('PUT', `/api/warehouses/${id(warehouseId)}`, { body: input, etag }),
  setWarehouseActive: (warehouseId: string, active: boolean) =>
    send<Warehouse>('POST', `/api/warehouses/${id(warehouseId)}/${active ? 'activate' : 'deactivate'}`),
};

// ---------------------------------------------------------------- inventory
export interface TransferInput {
  reference: string;
  fromWarehouseId: string;
  toWarehouseId: string;
  note?: string | undefined;
  lines: { productId: string; quantity: number }[];
}

export interface AdjustmentInput {
  reference: string;
  warehouseId: string;
  reasonCode: AdjustmentReason;
  note: string;
  lines: { productId: string; quantityDelta: number }[];
}

export const inventoryApi = {
  balances: (
    q: { warehouseId: string; search?: string | undefined; lowStockOnly?: boolean | undefined; cursor?: string | undefined; limit?: number | undefined },
    signal?: AbortSignal,
  ) =>
    getJson<Page<Balance>>(
      '/api/inventory/balances',
      { warehouseId: q.warehouseId, search: q.search, lowStockOnly: q.lowStockOnly === true ? true : undefined, cursor: q.cursor, limit: q.limit },
      signal,
    ),
  movements: (
    q: {
      warehouseId: string;
      productId?: string | undefined;
      type?: MovementType | undefined;
      from?: string | undefined;
      to?: string | undefined;
      cursor?: string | undefined;
    },
    signal?: AbortSignal,
  ) => getJson<Page<Movement>>('/api/inventory/movements', { ...q }, signal),
  reservations: (q: { warehouseId: string; productId?: string | undefined; cursor?: string | undefined }, signal?: AbortSignal) =>
    getJson<Page<Reservation>>('/api/inventory/reservations', { ...q }, signal),
  transfers: (q: { warehouseId: string; cursor?: string | undefined }, signal?: AbortSignal) =>
    getJson<Page<Transfer>>('/api/inventory/transfers', { ...q }, signal),
  createTransfer: (input: TransferInput, key: string) =>
    send<Transfer>('POST', '/api/inventory/transfers', { body: compact({ ...input }), key }),
  adjustments: (q: { warehouseId: string; cursor?: string | undefined }, signal?: AbortSignal) =>
    getJson<Page<Adjustment>>('/api/inventory/adjustments', { ...q }, signal),
  createAdjustment: (input: AdjustmentInput, key: string) => send<Adjustment>('POST', '/api/inventory/adjustments', { body: input, key }),
};

// ---------------------------------------------------------------- sales
export interface SalesOrderInput {
  customerId: string;
  warehouseId: string;
  note?: string | undefined;
  lines: { productId: string; quantity: number }[];
}

export interface FulfilmentInputLine {
  orderLineId: string;
  quantity: number;
}

export interface OrderListQuery<S extends string> {
  warehouseId?: string | undefined;
  status?: S | undefined;
  mine?: boolean | undefined;
  cursor?: string | undefined;
  limit?: number | undefined;
}

export const salesApi = {
  list: (q: OrderListQuery<SalesOrderStatus>, signal?: AbortSignal) =>
    getJson<Page<SalesOrderListItem>>(
      '/api/sales/orders',
      { warehouseId: q.warehouseId, status: q.status, mine: q.mine === true ? true : undefined, cursor: q.cursor, limit: q.limit },
      signal,
    ),
  get: (orderId: string, signal?: AbortSignal) => getJson<SalesOrder>(`/api/sales/orders/${id(orderId)}`, undefined, signal),
  summary: (orderId: string, signal?: AbortSignal) =>
    getJson<SalesOrderSummary>(`/api/sales/orders/${id(orderId)}/summary`, undefined, signal),
  create: (input: SalesOrderInput, key: string) => send<SalesOrder>('POST', '/api/sales/orders', { body: compact({ ...input }), key }),
  update: (orderId: string, input: SalesOrderInput, etag: string) =>
    send<SalesOrder>('PUT', `/api/sales/orders/${id(orderId)}`, { body: compact({ ...input }), etag }),
  confirm: (orderId: string, etag: string, key: string) =>
    send<SalesOrder>('POST', `/api/sales/orders/${id(orderId)}/confirm`, { etag, key }),
  cancel: (orderId: string, reason: string, etag: string, key: string) =>
    send<SalesOrder>('POST', `/api/sales/orders/${id(orderId)}/cancel`, { body: { reason }, etag, key }),
  /** No If-Match on purpose: the server guards a shipment by the remaining quantity. */
  ship: (orderId: string, dispatchReference: string, lines: FulfilmentInputLine[], key: string) =>
    send<Shipment>('POST', `/api/sales/orders/${id(orderId)}/shipments`, { body: { dispatchReference, lines }, key }),
};

// ---------------------------------------------------------------- purchasing
export interface PurchaseOrderInput {
  supplierId: string;
  warehouseId: string;
  note?: string | undefined;
  lines: { productId: string; quantity: number; unitCost: number }[];
}

export const purchasingApi = {
  list: (q: OrderListQuery<PurchaseOrderStatus>, signal?: AbortSignal) =>
    getJson<Page<PurchaseOrderListItem>>(
      '/api/purchasing/orders',
      { warehouseId: q.warehouseId, status: q.status, mine: q.mine === true ? true : undefined, cursor: q.cursor, limit: q.limit },
      signal,
    ),
  get: (orderId: string, signal?: AbortSignal) => getJson<PurchaseOrder>(`/api/purchasing/orders/${id(orderId)}`, undefined, signal),
  create: (input: PurchaseOrderInput, key: string) =>
    send<PurchaseOrder>('POST', '/api/purchasing/orders', { body: compact({ ...input }), key }),
  update: (orderId: string, input: PurchaseOrderInput, etag: string) =>
    send<PurchaseOrder>('PUT', `/api/purchasing/orders/${id(orderId)}`, { body: compact({ ...input }), etag }),
  approve: (orderId: string, etag: string, key: string) =>
    send<PurchaseOrder>('POST', `/api/purchasing/orders/${id(orderId)}/approve`, { etag, key }),
  cancel: (orderId: string, reason: string, etag: string, key: string) =>
    send<PurchaseOrder>('POST', `/api/purchasing/orders/${id(orderId)}/cancel`, { body: { reason }, etag, key }),
  /** No If-Match on purpose: the server guards a receipt by the remaining quantity. */
  receive: (orderId: string, externalReference: string, lines: FulfilmentInputLine[], key: string) =>
    send<Receipt>('POST', `/api/purchasing/orders/${id(orderId)}/receipts`, { body: { externalReference, lines }, key }),
};

// ---------------------------------------------------------------- support
export interface TicketInput {
  title: string;
  description: string;
  priority: TicketPriority;
  module: AppModule;
  screen?: string | undefined;
  errorCode?: string | undefined;
  relatedDocument?: { type: RelatedDocumentType; number: string } | undefined;
}

export const supportApi = {
  tickets: (
    q: { status?: TicketStatus | undefined; assignee?: 'me' | 'unassigned' | undefined; mine?: boolean | undefined; cursor?: string | undefined },
    signal?: AbortSignal,
  ) =>
    getJson<Page<TicketListItem>>(
      '/api/support/tickets',
      { status: q.status, assignee: q.assignee, mine: q.mine === true ? true : undefined, cursor: q.cursor },
      signal,
    ),
  /** Support staff only (403 otherwise): the people a ticket can be assigned to. */
  staff: (signal?: AbortSignal) => getJson<SupportStaff[]>('/api/support/staff', undefined, signal),
  ticket: (ticketId: string, signal?: AbortSignal) => getJson<Ticket>(`/api/support/tickets/${id(ticketId)}`, undefined, signal),
  createTicket: (input: TicketInput, key: string) => send<Ticket>('POST', '/api/support/tickets', { body: compact({ ...input }), key }),
  addComment: (ticketId: string, body: string, isInternal: boolean, key: string) =>
    send<Ticket>('POST', `/api/support/tickets/${id(ticketId)}/comments`, { body: { body, isInternal }, key }),
  saveSuggestion: (ticketId: string, answerId: string, key: string) =>
    send<Ticket>('POST', `/api/support/tickets/${id(ticketId)}/comments/from-assist`, { body: { answerId }, key }),
  assign: (ticketId: string, assigneeUserId: string, etag: string) =>
    send<Ticket>('POST', `/api/support/tickets/${id(ticketId)}/assign`, { body: { assigneeUserId }, etag }),
  setStatus: (ticketId: string, status: TicketStatus, etag: string) =>
    send<Ticket>('POST', `/api/support/tickets/${id(ticketId)}/status`, { body: { status }, etag }),
};

// ---------------------------------------------------------------- knowledge base
export interface KbDocumentInput {
  slug: string;
  visibility: KbVisibility;
  module: AppModule;
  title: string;
  bodyMarkdown: string;
  warehouseId?: string | undefined;
  errorCode?: string | undefined;
}

export const kbApi = {
  articles: (q: { search?: string | undefined; module?: AppModule | undefined; errorCode?: string | undefined }, signal?: AbortSignal) =>
    getJson<KbArticleListItem[]>('/api/kb/articles', { ...q }, signal),
  article: (slug: string, signal?: AbortSignal) => getJson<KbArticle>(`/api/kb/articles/${id(slug)}`, undefined, signal),
  documents: (signal?: AbortSignal) => getJson<KbDocument[]>('/api/kb/manage/documents', undefined, signal),
  document: (documentId: string, signal?: AbortSignal) =>
    getJson<KbDocument>(`/api/kb/manage/documents/${id(documentId)}`, undefined, signal),
  createDocument: (input: KbDocumentInput, key: string) =>
    send<KbDocument>('POST', '/api/kb/manage/documents', { body: compact({ ...input }), key }),
  addVersion: (documentId: string, title: string, bodyMarkdown: string, etag: string, key: string) =>
    send<KbDocument>('POST', `/api/kb/manage/documents/${id(documentId)}/versions`, { body: { title, bodyMarkdown }, etag, key }),
  publish: (documentId: string, versionNo: number, etag: string, key: string) =>
    send<KbDocument>('POST', `/api/kb/manage/documents/${id(documentId)}/publish`, { body: { versionNo }, etag, key }),
  retire: (documentId: string, etag: string, key: string) =>
    send<KbDocument>('POST', `/api/kb/manage/documents/${id(documentId)}/retire`, { etag, key }),
};

// ---------------------------------------------------------------- assistance
export const assistApi = {
  status: (signal?: AbortSignal) => getJson<ProviderInfo>('/api/assist/status', undefined, signal),
  /** POST because the query may contain personal data and must not end up in a URL. */
  search: (query: string) => send<AssistSource[]>('POST', '/api/assist/search', { body: { query } }),
  ask: (question: string, ticketId: string | undefined) =>
    send<AssistAnswer>('POST', '/api/assist/ask', { body: compact({ question, ticketId }) }),
};

// ---------------------------------------------------------------- admin
export const adminApi = {
  users: (signal?: AbortSignal) => getJson<AdminUser[]>('/api/admin/users', undefined, signal),
  user: (userId: string, signal?: AbortSignal) => getJson<AdminUser>(`/api/admin/users/${id(userId)}`, undefined, signal),
  /** Not idempotency-keyed by contract: the response carries a temporary password and is never stored. */
  createUser: (input: { userName: string; displayName: string; roles: Role[]; poApprovalLimit: number }) =>
    send<TemporaryPassword>('POST', '/api/admin/users', { body: input }),
  setRoles: (userId: string, roles: Role[], poApprovalLimit: number, etag: string) =>
    send<AdminUser>('PUT', `/api/admin/users/${id(userId)}/roles`, { body: { roles, poApprovalLimit }, etag }),
  setGrants: (userId: string, grants: Grant[], etag: string) =>
    send<AdminUser>('PUT', `/api/admin/users/${id(userId)}/warehouse-grants`, { body: { grants }, etag }),
  resetPassword: (userId: string) => send<TemporaryPassword>('POST', `/api/admin/users/${id(userId)}/reset-password`),
  setActive: (userId: string, active: boolean) =>
    send<AdminUser>('POST', `/api/admin/users/${id(userId)}/${active ? 'activate' : 'deactivate'}`),
  audit: (q: { action?: string | undefined; entityType?: string | undefined; cursor?: string | undefined }, signal?: AbortSignal) =>
    getJson<Page<AuditEntry>>('/api/admin/audit', { ...q }, signal),
};

// ---------------------------------------------------------------- operations
export const opsApi = {
  outbox: (signal?: AbortSignal) => getJson<OutboxSummary>('/api/ops/outbox', undefined, signal),
  /** 204 (no body) until a reconciliation has run. */
  latestReconciliation: async (signal?: AbortSignal): Promise<ReconciliationRun | null> =>
    (await getJson<ReconciliationRun | undefined>('/api/ops/reconciliation/latest', undefined, signal)) ?? null,
  runReconciliation: (key: string) => send<ReconciliationRun>('POST', '/api/ops/reconciliation/run', { key }),
  metrics: (signal?: AbortSignal) => getJson<Metrics>('/api/ops/metrics', undefined, signal),
  notifications: (signal?: AbortSignal) => getJson<AppNotification[]>('/api/notifications', undefined, signal),
  markNotificationRead: (notificationId: string) => send<void>('POST', `/api/notifications/${id(notificationId)}/read`),
};
