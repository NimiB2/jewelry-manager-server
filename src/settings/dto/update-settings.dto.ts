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
  // isPermanent (card fee, VAT, fixed costs) hides the delete button client-side — same
  // pattern as Collection.isPermanent — so the fixed-expense-rate inputs the pricing
  // formula relies on can't be removed by accident; freely-added fees stay deletable.
  @IsOptional()
  @IsArray()
  feesItems?: { name: string; percent: number; isPermanent?: boolean }[];

  @IsOptional()
  @IsNumber()
  profitFloorPercent?: number;

  @IsOptional()
  @IsArray()
  @IsString({ each: true })
  preparationStages?: string[];
}
