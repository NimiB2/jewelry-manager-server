import { Injectable } from '@nestjs/common';
import { PrismaService } from '../prisma/prisma.service.js';
import { TenantContext } from '../tenant/tenant-context.js';
import { Role } from '../generated/prisma/enums.js';
import { CreateUserDto } from './dto/create-user.dto.js';

@Injectable()
export class UsersService {
  constructor(private readonly prisma: PrismaService) {}

  // Auto-scoped to the current business by the Prisma tenant extension.
  getUsers() {
    return this.prisma.client.user.findMany({
      select: { id: true, name: true, email: true, phone: true, role: true, createdAt: true },
      orderBy: { createdAt: 'asc' },
    });
  }

  // Pre-creates a placeholder record by email — no firebaseUid yet. It gets
  // linked automatically the first time that person signs in with Google
  // (see FirebaseAuthGuard), so there's no invitation email to send here.
  createUser(dto: CreateUserDto) {
    return this.prisma.client.user.create({
      data: {
        email: dto.email,
        name: dto.name,
        phone: dto.phone,
        role: dto.role ?? Role.EMPLOYEE,
        businessId: TenantContext.getBusinessId()!,
      },
      select: { id: true, name: true, email: true, phone: true, role: true, createdAt: true },
    });
  }
}
