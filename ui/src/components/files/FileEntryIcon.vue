<script setup lang="ts">
import { computed } from 'vue'
import { Document, Folder, Picture } from '@element-plus/icons-vue'
import { buildThumbnailUrl } from '@/api/files'
import { hasImageThumbnail, isImageFile } from '@/config/imageTypes'

/**
 * 文件条目的 32×32 图标：目录用文件夹图标，可生成缩略图的图片复用「图片浏览」的
 * 后端缩略图接口（懒加载 + 磁盘缓存），其他图片与文件回退为图片/文档图标。
 * 完全由 props 驱动，供 el-table-v2 回收/复用行组件时安全使用。
 */
const props = defineProps<{
  name: string
  path: string
  isDirectory: boolean
  modifiedAt: number
}>()

const isImage = computed(() => !props.isDirectory && isImageFile(props.name))

/** SVG/AVIF 等后端无法解码的类型不请求缩略图，直接显示图片图标。 */
const thumbnailUrl = computed(() =>
  isImage.value && hasImageThumbnail(props.name)
    ? buildThumbnailUrl(props.path, props.modifiedAt)
    : '',
)
</script>

<template>
  <span class="entry-icon" :class="{ folder: isDirectory, picture: isImage }">
    <el-icon v-if="isDirectory"><Folder /></el-icon>
    <el-image
      v-else-if="thumbnailUrl"
      class="thumb"
      :src="thumbnailUrl"
      fit="cover"
      lazy
    >
      <template #placeholder>
        <span class="thumb-fallback"><el-icon><Picture /></el-icon></span>
      </template>
      <template #error>
        <span class="thumb-fallback"><el-icon><Picture /></el-icon></span>
      </template>
    </el-image>
    <el-icon v-else-if="isImage"><Picture /></el-icon>
    <el-icon v-else><Document /></el-icon>
  </span>
</template>

<style scoped>
.entry-icon {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  color: #909399;
  font-size: 16px;
}

.entry-icon.folder {
  color: #e6a23c;
}

.entry-icon.picture {
  color: #409eff;
}

.thumb {
  display: block;
  width: 32px;
  height: 32px;
  overflow: hidden;
  border-radius: 4px;
  background: #f5f7fa;
}

.thumb :deep(img) {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.thumb-fallback {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  color: #909399;
  font-size: 16px;
}
</style>
