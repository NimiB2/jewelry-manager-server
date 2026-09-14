export type PricingFormulaInput = {
  metalWeightGrams: number;
  metalPricePerGram: number;
  laborHours: number;
  laborHourlyRate: number;
  stonesCost: number;
  additionalProductionCost: number;
  shippingCost: number;
  packagingCost: number;
  /** שיעור הוצאות קבועות — fraction added on top of direct costs, e.g. 0.17 for 17%. */
  fixedExpenseRate: number;
  /**
   * מקדם רווח — a markup multiplier (e.g. 1.5), not a profit percentage.
   * The resulting profit rate (profit ÷ price excl. VAT) works out to
   * `1 − 1/profitMultiplier`, independent of the card-fee and VAT rates below —
   * the formula grosses up the price so those two never erode this margin.
   */
  profitMultiplier: number;
  /** שיעור סליקה — card-processing fee, charged on the final VAT-inclusive price. */
  cardFeeRate: number;
  /** שיעור מע"מ */
  vatRate: number;
};

export type PricingFormulaResult = {
  metalCost: number;
  laborCost: number;
  directCosts: number;
  costWithFixedExpenses: number;
  priceExclVat: number;
  finalPriceInclVat: number;
  cardFeeCost: number;
  pricingProfit: number;
  profitRate: number;
};

/**
 * The one and only place the sale price is derived from cost. Deliberately not
 * settings-driven in its structure (unlike the numbers it takes as input) —
 * this is core financial logic, not a user-editable formula tree.
 *
 * Throws instead of returning a nonsensical price when profitMultiplier is too
 * low relative to the card-fee/VAT load: at that point the denominator below
 * hits zero or goes negative, which would otherwise silently produce an
 * infinite or negative price.
 */
export function calculatePricingFormula(
  input: PricingFormulaInput,
): PricingFormulaResult {
  const {
    metalWeightGrams,
    metalPricePerGram,
    laborHours,
    laborHourlyRate,
    stonesCost,
    additionalProductionCost,
    shippingCost,
    packagingCost,
    fixedExpenseRate,
    profitMultiplier,
    cardFeeRate,
    vatRate,
  } = input;

  if (profitMultiplier <= 1) {
    throw new Error('מקדם רווח חייב להיות גדול מ-1');
  }

  const denominator = 1 / profitMultiplier - cardFeeRate * (1 + vatRate);
  if (denominator <= 0) {
    throw new Error(
      'מקדם הרווח נמוך מדי יחסית לעמלת הסליקה והמע"מ — לא ניתן לחשב מחיר',
    );
  }

  const metalCost = metalWeightGrams * metalPricePerGram;
  const laborCost = laborHours * laborHourlyRate;
  const directCosts =
    metalCost +
    laborCost +
    stonesCost +
    additionalProductionCost +
    shippingCost +
    packagingCost;
  const costWithFixedExpenses = directCosts * (1 + fixedExpenseRate);

  const priceExclVat = costWithFixedExpenses / denominator;
  const finalPriceInclVat = priceExclVat * (1 + vatRate);
  const cardFeeCost = finalPriceInclVat * cardFeeRate;
  const pricingProfit = priceExclVat - costWithFixedExpenses - cardFeeCost;
  const profitRate = pricingProfit / priceExclVat;

  return {
    metalCost,
    laborCost,
    directCosts,
    costWithFixedExpenses,
    priceExclVat,
    finalPriceInclVat,
    cardFeeCost,
    pricingProfit,
    profitRate,
  };
}
