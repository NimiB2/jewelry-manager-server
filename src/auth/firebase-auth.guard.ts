import {
  CanActivate,
  ExecutionContext,
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { FirebaseService } from '../firebase/firebase.service.js';
import { PrismaService } from '../prisma/prisma.service.js';
import { IS_PUBLIC_KEY } from './public.decorator.js';

@Injectable()
export class FirebaseAuthGuard implements CanActivate {
  constructor(
    private readonly firebase: FirebaseService,
    private readonly prisma: PrismaService,
    private readonly reflector: Reflector,
  ) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const isPublic = this.reflector.getAllAndOverride<boolean>(
      IS_PUBLIC_KEY,
      [context.getHandler(), context.getClass()],
    );
    if (isPublic) {
      return true;
    }

    const request = context.switchToHttp().getRequest();
    const authHeader = request.headers['authorization'];

    if (!authHeader?.startsWith('Bearer ')) {
      throw new UnauthorizedException('Missing bearer token');
    }

    const token = authHeader.slice('Bearer '.length);

    let decoded;
    try {
      decoded = await this.firebase.verifyIdToken(token);
    } catch {
      throw new UnauthorizedException('Invalid token');
    }

    let user = await this.prisma.client.user.findUnique({
      where: { firebaseUid: decoded.uid },
    });

    if (!user && decoded.email) {
      // First sign-in for an employee an owner pre-created by email — link
      // this Firebase identity to that pending record instead of rejecting.
      const invited = await this.prisma.client.user.findFirst({
        where: { firebaseUid: null, email: decoded.email },
      });
      if (invited) {
        user = await this.prisma.client.user.update({
          where: { id: invited.id },
          data: { firebaseUid: decoded.uid },
        });
      }
    }

    if (!user) {
      throw new UnauthorizedException('User not recognized');
    }

    request.currentUser = user;
    return true;
  }
}
