import { TenantContext } from '../tenant/tenant-context.js';

// Only these models carry their own businessId directly. Junction/child
// tables (ProductCollection, OrderLineItem) are scoped through their parent
// instead, so they're deliberately left out.
const TENANT_MODELS = new Set([
  'User',
  'Product',
  'Collection',
  'Settings',
  'Order',
  'Expense',
  'Income',
]);

// Loosely typed on purpose: Prisma's extension generics are hard to express
// precisely across every model/operation, and this runs identically for all
// of them — a runtime check against TENANT_MODELS, not per-model code.
export const tenantScopedExtension = {
  name: 'tenantScoped',
  query: {
    $allModels: {
      async $allOperations({ model, operation, args, query }: any) {
        if (!TENANT_MODELS.has(model)) {
          return query(args);
        }

        const businessId = TenantContext.getBusinessId();
        if (!businessId) {
          // No request context (e.g. the guard's own lookup, before the
          // business is known) — let it through unscoped.
          return query(args);
        }

        if (operation === 'create') {
          args.data = { ...args.data, businessId };
        } else if (operation === 'createMany' && Array.isArray(args.data)) {
          args.data = args.data.map((row: any) => ({ ...row, businessId }));
        } else {
          args.where = { ...args.where, businessId };
        }

        return query(args);
      },
    },
  },
};
