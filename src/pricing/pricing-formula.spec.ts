import { describe, expect, it } from 'vitest';
import { calculatePricingFormula } from './pricing-formula.js';

const baseInput = {
  metalWeightGrams: 5,
  metalPricePerGram: 10,
  laborHours: 2,
  laborHourlyRate: 50,
  stonesCost: 20,
  additionalProductionCost: 5,
  shippingCost: 10,
  packagingCost: 15,
  fixedExpenseRate: 0,
  profitMultiplier: 1.5,
  cardFeeRate: 0.03,
  vatRate: 0.17,
};

describe('calculatePricingFormula', () => {
  it('nets the same profit rate as 1 - 1/profitMultiplier, regardless of card fee and VAT', () => {
    const result = calculatePricingFormula(baseInput);
    expect(result.profitRate).toBeCloseTo(
      1 - 1 / baseInput.profitMultiplier,
      6,
    );
  });

  it('matches a hand-checked example', () => {
    const result = calculatePricingFormula({
      ...baseInput,
      fixedExpenseRate: 0.17,
    });
    // directCosts = 50 + 100 + 20 + 5 + 10 + 15 = 200; ×1.17 = 234
    expect(result.directCosts).toBeCloseTo(200, 6);
    expect(result.costWithFixedExpenses).toBeCloseTo(234, 6);
    // denominator = 1/1.5 - 0.03×1.17 = 0.6667 - 0.0351 = 0.6316
    // priceExclVat = 234 / 0.6316 ≈ 370.5
    expect(result.priceExclVat).toBeCloseTo(370.5, 0);
  });

  it('throws instead of returning an infinite/negative price when profitMultiplier is too low', () => {
    expect(() =>
      calculatePricingFormula({
        ...baseInput,
        profitMultiplier: 1.01,
        cardFeeRate: 0.9,
        vatRate: 0.5,
      }),
    ).toThrow();
  });

  it('throws when profitMultiplier is not above 1', () => {
    expect(() =>
      calculatePricingFormula({ ...baseInput, profitMultiplier: 1 }),
    ).toThrow();
  });
});
