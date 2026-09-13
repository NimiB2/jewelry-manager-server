import { IsNotEmpty, IsString } from 'class-validator';

export class UpdateCollectionDto {
  @IsString()
  @IsNotEmpty()
  name!: string;
}
