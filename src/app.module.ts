import { Module } from '@nestjs/common';
import { APP_GUARD, APP_INTERCEPTOR } from '@nestjs/core';
import { AppController } from './app.controller.js';
import { AppService } from './app.service.js';
import { FirebaseAuthGuard } from './auth/firebase-auth.guard.js';
import { RolesGuard } from './auth/roles.guard.js';
import { FirebaseModule } from './firebase/firebase.module.js';
import { PrismaModule } from './prisma/prisma.module.js';
import { TenantInterceptor } from './tenant/tenant.interceptor.js';

@Module({
  imports: [PrismaModule, FirebaseModule],
  controllers: [AppController],
  providers: [
    AppService,
    { provide: APP_GUARD, useClass: FirebaseAuthGuard },
    { provide: APP_GUARD, useClass: RolesGuard },
    { provide: APP_INTERCEPTOR, useClass: TenantInterceptor },
  ],
})
export class AppModule {}
