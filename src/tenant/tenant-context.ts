import { AsyncLocalStorage } from 'node:async_hooks';

interface TenantStore {
  businessId: string | null;
}

// Node's per-request storage — lets any Prisma query find the current
// request's businessId without it being passed down explicitly.
const storage = new AsyncLocalStorage<TenantStore>();

export const TenantContext = {
  // Synchronous entry point — used where the caller triggers execution
  // itself inside the callback (e.g. calling .subscribe()).
  runSync<T>(businessId: string | null, callback: () => T): T {
    return storage.run({ businessId }, callback);
  },

  getBusinessId(): string | null {
    return storage.getStore()?.businessId ?? null;
  },
};
