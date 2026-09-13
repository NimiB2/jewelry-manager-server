import 'dotenv/config';
import { PrismaPg } from '@prisma/adapter-pg';
import { PrismaClient } from '../src/generated/prisma/client.js';

const adapter = new PrismaPg({ connectionString: process.env.DATABASE_URL });
const prisma = new PrismaClient({ adapter });

async function main() {
  const business = await prisma.business.upsert({
    where: { id: '00000000-0000-0000-0000-000000000001' },
    update: {},
    create: {
      id: '00000000-0000-0000-0000-000000000001',
      name: 'Meital Bar',
    },
  });

  await prisma.settings.upsert({
    where: { businessId: business.id },
    update: {},
    create: {
      businessId: business.id,
      data: {
        materials: {
          silver: { pricePerGram: 2.5, laborHoursPerGram: 0.4 },
          gold: { pricePerGram: 12, laborHoursPerGram: 0.5 },
        },
        laborHourRate: 100,
        packagingCost: 10,
        feesFactor: 1.17,
        profitMultiplier: 1.5,
        profitFloorPercent: 30,
        preparationStages: ['יציקה', 'שיבוץ אבנים', 'ליטוש', 'ניקוי'],
      },
    },
  });

  console.log({ business });
}

main()
  .then(async () => {
    await prisma.$disconnect();
  })
  .catch(async (e) => {
    console.error(e);
    await prisma.$disconnect();
    process.exit(1);
  });
