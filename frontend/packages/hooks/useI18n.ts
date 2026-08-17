import type { NDateLocale, NLocale } from 'naive-ui'
import { dateEnUS, dateJaJP, dateZhCN, enUS, jaJP, zhCN } from 'naive-ui'
import { computed } from 'vue'
import { useAppStore } from '~/stores'

interface NaiveLocaleEntry { ui: NLocale, date: NDateLocale }

// 未登记语言的兜底：回退英文而非中文，否则中文组件文案会混进外语界面。
// 抽成具名常量而非内联，是为了让索引取值的 undefined 分支能收窄到确定类型。
const FALLBACK_NAIVE_LOCALE: NaiveLocaleEntry = { ui: enUS, date: dateEnUS }

/**
 * locale → naive-ui 语系对象。新增语言在此加一行即可。
 * 早先是 `locale === 'zh-CN' ? zhCN : enUS` 的三元式，多加一个语言就要改判断。
 */
const NAIVE_LOCALES: Record<string, NaiveLocaleEntry> = {
  'zh-CN': { ui: zhCN, date: dateZhCN },
  'en-US': FALLBACK_NAIVE_LOCALE,
  'ja-JP': { ui: jaJP, date: dateJaJP },
}

export function useNaiveLocale() {
  const appStore = useAppStore()
  const locale = computed(() => appStore.locale)

  const entry = computed(() => NAIVE_LOCALES[locale.value] ?? FALLBACK_NAIVE_LOCALE)
  const naiveLocale = computed(() => entry.value.ui)
  const naiveDateLocale = computed(() => entry.value.date)

  return {
    locale,
    naiveLocale,
    naiveDateLocale,
  }
}

export function useLocale() {
  const appStore = useAppStore()

  const locale = computed(() => appStore.locale)

  // 只改偏好 ref，vue-i18n 由 store 内的 watch 跟随。
  // 这里若再手动赋一次，切换语言就有了两条路径，而其它设备推来的偏好只走 ref 那条——
  // 正是「提示已同步、界面语言不变」的成因。
  function setLocale(lang: string) {
    appStore.setLocale(lang)
  }

  return {
    locale,
    setLocale,
  }
}
