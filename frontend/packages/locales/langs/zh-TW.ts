// 只含 packages（admin shell）自身的文案命名空間。
// 應用業務文案（identity/setting/log/message/tenant/... 等）在 src/locales，
// 由 src 啟動時經 registerLocaleMessages() 合併進同一個 i18n 實例——
// 底層包不該知道本應用有哪些業務模組。
import checkUpdates from './zh-TW/check_updates'
import common from './zh-TW/common'
import component from './zh-TW/component'
import error from './zh-TW/error'
import header from './zh-TW/header'
import island from './zh-TW/island'
import menu from './zh-TW/menu'
import page from './zh-TW/page'
import preference from './zh-TW/preference'
import tabbar from './zh-TW/tabbar'

export default {
  common,
  component,
  menu,
  header,
  tabbar,
  preference,
  page,
  island,
  error,
  check_updates: checkUpdates,
}
