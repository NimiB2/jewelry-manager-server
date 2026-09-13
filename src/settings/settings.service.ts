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
    const mergedData = { ...(existing.data as Record<string, unknown>), ...update };

    return this.prisma.client.settings.update({
      where: { id: existing.id },
      data: { data: mergedData },
    });
  }
}
