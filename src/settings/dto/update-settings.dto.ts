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
  materials?: Record<string, { pricePerGram: number; laborHoursPerGram: number }>;

  @IsOptional()
  @IsNumber()
  laborHourRate?: number;

  @IsOptional()
  @IsNumber()
  packagingCost?: number;

  @IsOptional()
  @IsNumber()
  feesFactor?: number;

  @IsOptional()
  @IsNumber()
  profitMultiplier?: number;

  @IsOptional()
  @IsNumber()
  profitFloorPercent?: number;

  @IsOptional()
  @IsArray()
  @IsString({ each: true })
  preparationStages?: string[];
}
