<script setup lang="ts">
import { computed } from 'vue'

/**
 * 语言国旗图标。
 * 离线 iconify 仅预载 lucide/tabler/mdi（无国旗图标集），故内置 SVG 直绘，跨平台稳定显示。
 */
defineOptions({ name: 'LocaleFlag' })

const props = withDefaults(defineProps<{ locale: string, size?: number }>(), { size: 18 })

const width = computed(() => props.size)
const height = computed(() => Math.round((props.size * 2) / 3))
</script>

<template>
  <span
    class="locale-flag"
    :style="{ width: `${width}px`, height: `${height}px` }"
    aria-hidden="true"
  >
    <!-- 简体中文：中国国旗 -->
    <svg v-if="locale === 'zh-CN'" viewBox="0 0 30 20" xmlns="http://www.w3.org/2000/svg">
      <rect width="30" height="20" fill="#DE2910" />
      <g fill="#FFDE00">
        <polygon points="0,-1 0.2245,-0.309 0.951,-0.309 0.3633,0.118 0.5878,0.809 0,0.382 -0.5878,0.809 -0.3633,0.118 -0.951,-0.309 -0.2245,-0.309" transform="translate(5,5) scale(3)" />
        <polygon points="0,-1 0.2245,-0.309 0.951,-0.309 0.3633,0.118 0.5878,0.809 0,0.382 -0.5878,0.809 -0.3633,0.118 -0.951,-0.309 -0.2245,-0.309" transform="translate(10,2) scale(1)" />
        <polygon points="0,-1 0.2245,-0.309 0.951,-0.309 0.3633,0.118 0.5878,0.809 0,0.382 -0.5878,0.809 -0.3633,0.118 -0.951,-0.309 -0.2245,-0.309" transform="translate(12,4) scale(1)" />
        <polygon points="0,-1 0.2245,-0.309 0.951,-0.309 0.3633,0.118 0.5878,0.809 0,0.382 -0.5878,0.809 -0.3633,0.118 -0.951,-0.309 -0.2245,-0.309" transform="translate(12,7) scale(1)" />
        <polygon points="0,-1 0.2245,-0.309 0.951,-0.309 0.3633,0.118 0.5878,0.809 0,0.382 -0.5878,0.809 -0.3633,0.118 -0.951,-0.309 -0.2245,-0.309" transform="translate(10,9) scale(1)" />
      </g>
    </svg>

    <!-- English：美国国旗 -->
    <svg v-else-if="locale === 'en-US'" viewBox="0 0 30 20" xmlns="http://www.w3.org/2000/svg">
      <rect width="30" height="20" fill="#FFFFFF" />
      <g fill="#B22234">
        <rect y="0" width="30" height="1.54" />
        <rect y="3.08" width="30" height="1.54" />
        <rect y="6.15" width="30" height="1.54" />
        <rect y="9.23" width="30" height="1.54" />
        <rect y="12.31" width="30" height="1.54" />
        <rect y="15.38" width="30" height="1.54" />
        <rect y="18.46" width="30" height="1.54" />
      </g>
      <rect width="12" height="10.77" fill="#3C3B6E" />
      <g fill="#FFFFFF">
        <circle cx="2.2" cy="2.1" r="0.7" />
        <circle cx="4.6" cy="2.1" r="0.7" />
        <circle cx="7" cy="2.1" r="0.7" />
        <circle cx="9.4" cy="2.1" r="0.7" />
        <circle cx="3.4" cy="4.1" r="0.7" />
        <circle cx="5.8" cy="4.1" r="0.7" />
        <circle cx="8.2" cy="4.1" r="0.7" />
        <circle cx="2.2" cy="6.1" r="0.7" />
        <circle cx="4.6" cy="6.1" r="0.7" />
        <circle cx="7" cy="6.1" r="0.7" />
        <circle cx="9.4" cy="6.1" r="0.7" />
        <circle cx="3.4" cy="8.1" r="0.7" />
        <circle cx="5.8" cy="8.1" r="0.7" />
        <circle cx="8.2" cy="8.1" r="0.7" />
      </g>
    </svg>

    <!-- 日本語：日本国旗（日章旗：白地、中央に紅の円、直径は高さの 3/5） -->
    <svg v-else-if="locale === 'ja-JP'" viewBox="0 0 30 20" xmlns="http://www.w3.org/2000/svg">
      <rect width="30" height="20" fill="#FFFFFF" />
      <circle cx="15" cy="10" r="6" fill="#BC002D" />
    </svg>

    <!--
      한국어：대한민국 국기（태극기）。图标尺寸下四卦只有约 2px，
      按几何构造简化绘制：太极圆 + 四组卦画（棒与半径垂直，符合国旗构图）。
      左上乾 ☰ / 左下리 ☲ / 右上감 ☵ / 右下곤 ☷。
    -->
    <svg v-else-if="locale === 'ko-KR'" viewBox="0 0 30 20" xmlns="http://www.w3.org/2000/svg">
      <rect width="30" height="20" fill="#FFFFFF" />
      <!-- 太极：红上蓝下，轴线倾斜 33.69°（国旗规格） -->
      <g transform="rotate(-33.69 15 10)">
        <path d="M10,10 A5,5 0 0,1 20,10 A2.5,2.5 0 0,1 15,10 A2.5,2.5 0 0,0 10,10 Z" fill="#CD2E3A" />
        <path d="M10,10 A5,5 0 0,0 20,10 A2.5,2.5 0 0,1 15,10 A2.5,2.5 0 0,0 10,10 Z" fill="#0047A0" />
      </g>
      <g fill="#000000">
        <!-- 乾 ☰ 三连（左上） -->
        <g transform="translate(6.2 4.5) rotate(122)">
          <rect x="-1.8" y="-1.2" width="3.6" height="0.6" />
          <rect x="-1.8" y="-0.3" width="3.6" height="0.6" />
          <rect x="-1.8" y="0.6" width="3.6" height="0.6" />
        </g>
        <!-- 리 ☲ 连断连（左下） -->
        <g transform="translate(6.2 15.5) rotate(58)">
          <rect x="-1.8" y="-1.2" width="3.6" height="0.6" />
          <rect x="-1.8" y="-0.3" width="1.45" height="0.6" />
          <rect x="0.35" y="-0.3" width="1.45" height="0.6" />
          <rect x="-1.8" y="0.6" width="3.6" height="0.6" />
        </g>
        <!-- 감 ☵ 断连断（右上） -->
        <g transform="translate(23.8 4.5) rotate(58)">
          <rect x="-1.8" y="-1.2" width="1.45" height="0.6" />
          <rect x="0.35" y="-1.2" width="1.45" height="0.6" />
          <rect x="-1.8" y="-0.3" width="3.6" height="0.6" />
          <rect x="-1.8" y="0.6" width="1.45" height="0.6" />
          <rect x="0.35" y="0.6" width="1.45" height="0.6" />
        </g>
        <!-- 곤 ☷ 三断（右下） -->
        <g transform="translate(23.8 15.5) rotate(122)">
          <rect x="-1.8" y="-1.2" width="1.45" height="0.6" />
          <rect x="0.35" y="-1.2" width="1.45" height="0.6" />
          <rect x="-1.8" y="-0.3" width="1.45" height="0.6" />
          <rect x="0.35" y="-0.3" width="1.45" height="0.6" />
          <rect x="-1.8" y="0.6" width="1.45" height="0.6" />
          <rect x="0.35" y="0.6" width="1.45" height="0.6" />
        </g>
      </g>
    </svg>

    <!--
      繁體中文：中華民國國旗（青天白日滿地紅）。
      紅底 + 左上藍色青天（寬高各占一半）+ 十二芒白日 + 藍環 + 白色圓心。
    -->
    <svg v-else-if="locale === 'zh-TW'" viewBox="0 0 30 20" xmlns="http://www.w3.org/2000/svg">
      <rect width="30" height="20" fill="#FE0000" />
      <rect width="15" height="10" fill="#000095" />
      <polygon
        fill="#FFFFFF"
        points="10.90,5.00 9.34,5.49 10.44,6.70 8.84,6.34 9.20,7.94 7.99,6.84 7.50,8.40 7.01,6.84 5.80,7.94 6.16,6.34 4.56,6.70 5.67,5.49 4.10,5.00 5.67,4.51 4.56,3.30 6.16,3.66 5.80,2.06 7.01,3.17 7.50,1.60 7.99,3.17 9.20,2.06 8.84,3.66 10.44,3.30 9.34,4.51"
      />
      <circle cx="7.5" cy="5" r="1.75" fill="#000095" />
      <circle cx="7.5" cy="5" r="1.45" fill="#FFFFFF" />
    </svg>
  </span>
</template>

<style scoped>
.locale-flag {
  display: inline-block;
  overflow: hidden;
  line-height: 0;
  border-radius: 2px;
  box-shadow: 0 0 0 1px hsl(var(--border));
  vertical-align: middle;
}

.locale-flag svg {
  display: block;
  width: 100%;
  height: 100%;
}
</style>
