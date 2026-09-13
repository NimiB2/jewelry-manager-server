import {
  CallHandler,
  ExecutionContext,
  Injectable,
  NestInterceptor,
} from '@nestjs/common';
import { Observable } from 'rxjs';
import { TenantContext } from './tenant-context.js';

// Runs after the auth guard, so request.currentUser is already set.
// Everything the route handler does — including Prisma queries several
// layers down — runs inside this businessId context.
@Injectable()
export class TenantInterceptor implements NestInterceptor {
  intercept(context: ExecutionContext, next: CallHandler): Observable<any> {
    const request = context.switchToHttp().getRequest();
    const businessId = request.currentUser?.businessId ?? null;

    // Subscribing is what actually runs the route handler, so it has to
    // happen inside runSync — returning the Observable unsubscribed
    // wouldn't keep the context alive until execution actually starts.
    return new Observable((subscriber) => {
      TenantContext.runSync(businessId, () => {
        next.handle().subscribe(subscriber);
      });
    });
  }
}
