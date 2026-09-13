import { Body, Controller, Delete, Get, Param, Patch, Post } from '@nestjs/common';
import { CollectionsService } from './collections.service.js';
import { CreateCollectionDto } from './dto/create-collection.dto.js';
import { UpdateCollectionDto } from './dto/update-collection.dto.js';

@Controller('collections')
export class CollectionsController {
  constructor(private readonly collectionsService: CollectionsService) {}

  @Get()
  getCollections() {
    return this.collectionsService.getCollections();
  }

  @Post()
  createCollection(@Body() dto: CreateCollectionDto) {
    return this.collectionsService.createCollection(dto);
  }

  @Patch(':id')
  renameCollection(@Param('id') id: string, @Body() dto: UpdateCollectionDto) {
    return this.collectionsService.renameCollection(id, dto);
  }

  @Delete(':id')
  deleteCollection(@Param('id') id: string) {
    return this.collectionsService.deleteCollection(id);
  }
}
