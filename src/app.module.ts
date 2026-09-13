import { Module } from '@nestjs/common';
import { APP_GUARD, APP_INTERCEPTOR } from '@nestjs/core';
import { AppController } from './app.controller.js';
import { AppService } from './app.service.js';
import { FirebaseAuthGuard } from './auth/firebase-auth.guard.js';
import { RolesGuard } from './auth/roles.guard.js';
import { CollectionsModule } from './collections/collections.module.js';
import { FirebaseModule } from './firebase/firebase.module.js';
import { PrismaModule } from './prisma/prisma.module.js';
import { SettingsModule } from './settings/settings.module.js';
import { TenantInterceptor } from './tenant/tenant.interceptor.js';
import { UsersModule } from './users/users.module.js';

@Module({
  imports: [PrismaModule, FirebaseModule, SettingsModule, CollectionsModule, UsersModule],
  controllers: [AppController],
  providers: [
    AppService,
    { provide: APP_GUARD, useClass: FirebaseAuthGuard },
    { provide: APP_GUARD, useClass: RolesGuard },
    { provide: APP_INTERCEPTOR, useClass: TenantInterceptor },
  ],
})
export class AppModule {}
