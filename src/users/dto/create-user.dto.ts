import { IsEmail, IsIn, IsOptional, IsString } from 'class-validator';
import { Role } from '../../generated/prisma/enums.js';

export class CreateUserDto {
  @IsEmail()
  email!: string;

  @IsOptional()
  @IsString()
  name?: string;

  @IsOptional()
  @IsString()
  phone?: string;

  // Only OWNER/EMPLOYEE are assignable here — SUPER_ADMIN is not handed out via this UI.
  @IsOptional()
  @IsIn([Role.OWNER, Role.EMPLOYEE])
  role?: typeof Role.OWNER | typeof Role.EMPLOYEE;
}
