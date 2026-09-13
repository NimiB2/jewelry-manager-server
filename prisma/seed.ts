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
          silver: { pricePerGram: 2.5, laborHoursPerGram: 0.4, profitMultiplier: 1.5 },
          gold: { pricePerGram: 12, laborHoursPerGram: 0.5, profitMultiplier: 1.8 },
        },
        laborHourRate: 100,
        pricingAdditions: [
          {
            name: 'אריזה',
            basePrice: 0,
            items: [
              { name: 'קופסת מתנה סטנדרטית', price: 8 },
              { name: 'שקית ממותגת', price: 3 },
            ],
          },
          { name: 'משלוח', basePrice: 0, items: [] },
        ],
        feesItems: [{ name: 'עלויות קבועות', percent: 17 }],
        profitFloorPercent: 30,
        preparationStages: ['יציקה', 'שיבוץ אבנים', 'ליטוש', 'ניקוי'],
        pricingFormula: {
          stages: [
            {
              id: 'costs',
              name: 'עלויות',
              terms: [
                { id: 'materialCost', key: 'materialCost', operator: null },
                { id: 'packagingCost', key: 'packagingCost', operator: '+' },
              ],
            },
            {
              id: 'labor',
              name: 'עבודה',
              terms: [
                { id: 'prev1', key: 'previousResult', operator: null },
                { id: 'laborCost', key: 'laborCost', operator: '+' },
              ],
            },
            {
              id: 'final',
              name: 'עמלות',
              terms: [
                { id: 'prev2', key: 'previousResult', operator: null },
                { id: 'fees', key: 'fees', operator: '×' },
              ],
            },
          ],
        },
      },
    },
  });

  const PERMANENT_COLLECTIONS = [
    { key: 'general', name: 'כללי' },
    { key: 'customOrder', name: 'הזמנה אישית' },
  ];
  for (const { key, name } of PERMANENT_COLLECTIONS) {
    const existing = await prisma.collection.findFirst({ where: { businessId: business.id, key } });
    if (!existing) {
      await prisma.collection.create({
        data: { businessId: business.id, name, key, isPermanent: true },
      });
    }
  }

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
