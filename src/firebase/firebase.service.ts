import { readFileSync } from 'node:fs';
import { Injectable } from '@nestjs/common';
import { type App, cert, initializeApp } from 'firebase-admin/app';
import { getAuth } from 'firebase-admin/auth';

@Injectable()
export class FirebaseService {
  private readonly app: App;

  constructor() {
    const path = process.env.FIREBASE_SERVICE_ACCOUNT_PATH;
    if (!path) {
      throw new Error('FIREBASE_SERVICE_ACCOUNT_PATH is not set');
    }

    const serviceAccount = JSON.parse(readFileSync(path, 'utf-8'));

    this.app = initializeApp({
      credential: cert(serviceAccount),
    });
  }

  verifyIdToken(token: string) {
    return getAuth(this.app).verifyIdToken(token);
  }
}
