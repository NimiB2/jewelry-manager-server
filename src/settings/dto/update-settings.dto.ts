import {
  IsArray,
  IsNumber,
  IsObject,
  IsOptional,
  IsString,
} from 'class-validator';

// Partial on purpose — PATCH /settings updates only the fields sent,
// leaving the rest of the settings tree untouched.
export class UpdateSettingsDto {
  @IsOptional()
  @IsObject()
  materials?: Record<
    string,
    { pricePerGram: number; laborHoursPerGram: number; profitMultiplier: number }
  >;

  @IsOptional()
  @IsNumber()
  laborHourRate?: number;

  // User-defined categories (packaging, shipping, etc.). Each has its own
  // base price plus an optional list of priced items — a category doesn't
  // need itemization to have a price. Both are freely editable.
  @IsOptional()
  @IsArray()
  pricingAdditions?: { name: string; basePrice: number; items: { name: string; price: number }[] }[];

  // Combined fee factor is derived from these (1 + sum(percent)/100), not a single flat number.
  @IsOptional()
  @IsArray()
  feesItems?: { name: string; percent: number }[];

  @IsOptional()
  @IsNumber()
  profitFloorPercent?: number;

  @IsOptional()
  @IsArray()
  @IsString({ each: true })
  preparationStages?: string[];

  // A user-built sequence of stages (each continuing from the previous
  // stage's result) that will drive the actual product-pricing calculator
  // once it's built. Standard × / ÷ before + / - precedence applies within
  // a stage — deliberately no manual parentheses.
  @IsOptional()
  @IsObject()
  pricingFormula?: {
    stages: {
      id: string;
      name: string;
      terms: {
        id: string;
        key: 'materialCost' | 'packagingCost' | 'laborCost' | 'fees' | 'previousResult';
        operator: '+' | '-' | '×' | '÷' | null;
      }[];
    }[];
  };
}
