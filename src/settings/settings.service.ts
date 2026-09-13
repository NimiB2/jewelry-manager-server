import { Injectable, NotFoundException } from '@nestjs/common';
import { PrismaService } from '../prisma/prisma.service.js';
import { UpdateSettingsDto } from './dto/update-settings.dto.js';

@Injectable()
export class SettingsService {
  constructor(private readonly prisma: PrismaService) {}

  async getSettings() {
    // Auto-scoped to the current business by the Prisma tenant extension.
    const settings = await this.prisma.client.settings.findFirst();
    if (!settings) {
      throw new NotFoundException('Settings not found for this business');
    }
    return settings;
  }

  async updateSettings(update: UpdateSettingsDto) {
    const existing = await this.getSettings();

    // class-transformer instantiates every declared DTO field (TS class fields
    // are own properties even when unset, under useDefineForClassFields — on
    // by default for our ES2023 target). A naive spread of `update` would
    // therefore overwrite every field the caller didn't send with `undefined`,
    // and JSON serialization then drops those keys — silently wiping the rest
    // of the settings tree on every single-field PATCH. Only merge keys that
    // were actually sent.
    const sentFields = Object.fromEntries(
      Object.entries(update).filter(([, value]) => value !== undefined),
    );

    // Merge in the database itself (jsonb `||`) rather than read-modify-write
    // in application code. Each settings section autosaves independently on
    // its own debounce timer, so two PATCH requests (e.g. materials and fees)
    // can easily overlap; a read-then-write here would let the slower request
    // silently overwrite the faster one's change with stale data.
    await this.prisma.client.$executeRaw`
      UPDATE "Settings" SET data = data || ${JSON.stringify(sentFields)}::jsonb WHERE id = ${existing.id}
    `;

    return this.prisma.client.settings.findUniqueOrThrow({ where: { id: existing.id } });
  }
}
