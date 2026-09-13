import { BadRequestException, Injectable, NotFoundException } from '@nestjs/common';
import { PrismaService } from '../prisma/prisma.service.js';
import { TenantContext } from '../tenant/tenant-context.js';
import { CreateCollectionDto } from './dto/create-collection.dto.js';
import { UpdateCollectionDto } from './dto/update-collection.dto.js';

@Injectable()
export class CollectionsService {
  constructor(private readonly prisma: PrismaService) {}

  // Auto-scoped to the current business by the Prisma tenant extension.
  getCollections() {
    return this.prisma.client.collection.findMany({ orderBy: { createdAt: 'asc' } });
  }

  createCollection(dto: CreateCollectionDto) {
    // businessId is also injected by the tenant extension at runtime, but the
    // create input type requires it (or the `business` relation) explicitly.
    return this.prisma.client.collection.create({
      data: { name: dto.name, businessId: TenantContext.getBusinessId()! },
    });
  }

  async renameCollection(id: string, dto: UpdateCollectionDto) {
    await this.findOrThrow(id);
    return this.prisma.client.collection.update({ where: { id }, data: { name: dto.name } });
  }

  async deleteCollection(id: string) {
    const collection = await this.findOrThrow(id);
    if (collection.isPermanent) {
      throw new BadRequestException('Cannot delete a permanent collection');
    }

    const general = await this.prisma.client.collection.findFirst({
      where: { key: 'general' },
    });
    if (general) {
      await this.moveProductsToGeneral(id, general.id);
    }

    // Cascades to the collection's own ProductCollection rows (schema onDelete: Cascade).
    return this.prisma.client.collection.delete({ where: { id } });
  }

  private async moveProductsToGeneral(fromCollectionId: string, generalId: string) {
    const links = await this.prisma.client.productCollection.findMany({
      where: { collectionId: fromCollectionId },
      select: { productId: true },
    });
    if (links.length === 0) return;

    const alreadyInGeneral = await this.prisma.client.productCollection.findMany({
      where: { collectionId: generalId, productId: { in: links.map((l) => l.productId) } },
      select: { productId: true },
    });
    const alreadyInGeneralIds = new Set(alreadyInGeneral.map((l) => l.productId));

    const toAdd = links
      .map((l) => l.productId)
      .filter((productId) => !alreadyInGeneralIds.has(productId));

    if (toAdd.length > 0) {
      await this.prisma.client.productCollection.createMany({
        data: toAdd.map((productId) => ({ productId, collectionId: generalId })),
      });
    }
  }

  private async findOrThrow(id: string) {
    const collection = await this.prisma.client.collection.findUnique({ where: { id } });
    if (!collection) throw new NotFoundException('Collection not found');
    return collection;
  }
}
