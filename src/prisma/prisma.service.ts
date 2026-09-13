import { Injectable, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { PrismaPg } from '@prisma/adapter-pg';
import { PrismaClient } from '../generated/prisma/client.js';
import { tenantScopedExtension } from './tenant-scoped.extension.js';

function createExtendedClient() {
  const client = new PrismaClient({
    adapter: new PrismaPg({ connectionString: process.env.DATABASE_URL }),
  });
  return client.$extends(tenantScopedExtension);
}

export type ExtendedPrismaClient = ReturnType<typeof createExtendedClient>;

// Composition instead of `extends PrismaClient`: $extends() returns a new
// client object, it doesn't modify the original in place, so there's
// nothing for a subclass to inherit the extension from.
@Injectable()
export class PrismaService implements OnModuleInit, OnModuleDestroy {
  readonly client: ExtendedPrismaClient = createExtendedClient();

  async onModuleInit() {
    await this.client.$connect();
  }

  async onModuleDestroy() {
    await this.client.$disconnect();
  }
}
