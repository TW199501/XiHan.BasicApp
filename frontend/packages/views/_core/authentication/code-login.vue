<script lang="ts" setup>
import type { FormRules } from '@xihan-ui/headless'
import { XhButton, XhFieldControl, XhFieldLabel, XhFieldRoot, XhFormFieldGroup, XhFormRoot, XhFormSubmitTrigger, XhPinInputInput, XhPinInputRoot } from '@xihan-ui/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { PhoneInput } from '~/components'
import { toast } from '~/composables'
import { useTheme } from '~/hooks'
import { useAppContext, useAuthStore } from '~/stores'
import CodeCountdown from '../shared/CodeCountdown.vue'
import { OTP_CODE_LENGTH, splitPinCode } from '../shared/pin-code'
import { useAuthFormInvalid } from './use-auth-form-invalid'

defineOptions({ name: 'CodeLoginPage' })

const { isDark } = useTheme()
const { t } = useI18n()
const authStore = useAuthStore()
const { apis } = useAppContext()
/** 重发倒计时这一轮的时长，大于 0 即正在倒计时 */
const resendSeconds = ref(0)

const formData = ref({
  /** E.164 手机号码，由 PhoneInput 组装吐出 */
  phone: '',
  code: '',
})
/** PhoneInput 的号码有效性；required 拦空值，这里拦「填了但格式不对」 */
const phoneValid = ref(false)

/**
 * 验证码的逐格值。表单里的 code 是拼接后的串（规则按长度校验、发码接口回填调试码都用它），
 * 格子这边是逐格数组，两边在此互转：格子每次改动把串写回表单，表单的串被外部整份改写
 * （回填调试码）时再拆回格子。用户逐格编辑不经串往返——见 splitPinCode 的说明。
 */
const codeCells = ref<string[]>([])

watch(() => formData.value.code, (code) => {
  if (code !== codeCells.value.join(''))
    codeCells.value = splitPinCode(code, OTP_CODE_LENGTH)
})

// 规则写成 computed：文案要跟着语言切换。组件库按 rule.message 优先、
// 没写则回落 validateMessages 模板，这里逐条给了文案就不需要模板
const rules = computed<FormRules>(() => ({
  phone: [
    // 校验规则首败即停：PhoneInput 对无效号码吐出的是空串，required 单看 formData.phone
    // 会把「填了但格式不对」误判成「没填」。validator 只认 phoneValid（空值在该组件里算有效），
    // 放在 required 前面，格式错误才能在 required 之前先被拦下、显示 phone_invalid 而非必填文案
    { validator: () => (phoneValid.value ? null : t('page.auth.phone_invalid')) },
    { required: true, message: t('page.auth.phone_placeholder') },
  ],
  code: [
    { required: true, message: t('page.auth.code_required') },
    // 组件库按 min/max 比长度，没有 len 这一档；两端同值即定长
    { min: OTP_CODE_LENGTH, max: OTP_CODE_LENGTH, message: t('page.auth.code_length_tip') },
  ],
}))

/**
 * 发验证码只关手机号这一个字段，而表单的公开 API 没有「单字段校验」这一项
 * （逐字段校验是 blur / change 模式下的内部动作）。这里就地判一次格式，
 * 提交那一路仍走表单自己的整表校验。
 */
function handleSendCode() {
  if (!phoneValid.value || !formData.value.phone) {
    toast.warning(t('page.auth.phone_invalid'))
    return
  }
  void (async () => {
    try {
      const response = await apis.sendPhoneLoginCodeApi(formData.value.phone)
      resendSeconds.value = 60
      if (response.debugCode) {
        formData.value.code = response.debugCode
      }
      toast.success(t('page.auth.code_sent'))
    }
    catch (err: unknown) {
      const error = err as { message?: string }
      toast.danger(error?.message || t('page.auth.code_send_failed'))
    }
  })()
}

/**
 * 校验通过表单才发 submit；被拦下走 invalid，错误文案由字段自己显。
 * 返回的 Promise 交给表单：落定前提交钮自己报在途、再按不重复提交，失败在这里接住
 */
async function onSubmit() {
  try {
    await authStore.loginByPhoneCode({
      phone: formData.value.phone,
      code: formData.value.code,
    })
  }
  catch (err: unknown) {
    const error = err as { message?: string }
    if (error?.message) {
      toast.danger(error.message)
    }
  }
}

const onAuthInvalid = useAuthFormInvalid()
</script>

<template>
  <div class="py-1">
    <div class="mb-8">
      <p
        class="mt-3 auth-body"
        :class="isDark ? 'text-gray-300' : 'text-[hsl(var(--muted-foreground))]'"
      >
        {{ t('page.auth.code_login_subtitle') }}
      </p>
    </div>

    <!-- 校验归表单：通过才发 submit，被拦下的错误由各字段的 error-text 自己显 -->
    <XhFormRoot
      v-model:values="formData"
      :rules="rules"
      validate-on="blur"
      @invalid="onAuthInvalid"
      @submit="onSubmit"
    >
      <!-- 字段靠占位文案表意，标签只留给读屏 -->
      <XhFormFieldGroup v-slot="{ value, setValue }" name="phone" class="!mb-6">
        <XhFieldRoot>
          <XhFieldLabel class="sr-only">
            {{ t('page.auth.phone_placeholder') }}
          </XhFieldLabel>
          <XhFieldControl>
            <PhoneInput
              size="lg"
              :value="(value as string)"
              @update:value="setValue"
              @valid="(v: boolean) => phoneValid = v"
            />
          </XhFieldControl>
        </XhFieldRoot>
      </XhFormFieldGroup>

      <XhFormFieldGroup v-slot="{ setValue }" name="code" class="!mb-6">
        <XhFieldRoot>
          <XhFieldLabel class="sr-only">
            {{ t('page.auth.code_required') }}
          </XhFieldLabel>
          <!-- 布局层留在控件外面：六格与发码钮同一行，放不下时钮换到下一行靠右。
               格子取缺省档：正方格的缺省档与 lg 档文本框、发码钮同一个控件高度，lg 档格子会高出一截 -->
          <div class="auth-code-row">
            <XhFieldControl>
              <XhPinInputRoot
                v-model:value="codeCells"
                :length="OTP_CODE_LENGTH"
                type="numeric"
                otp
                @value-change="setValue($event.valueAsString)"
              >
                <!-- 格间距长在格子自己身上，这层包裹只负责排成一行 -->
                <div style="display: flex">
                  <XhPinInputInput v-for="i in OTP_CODE_LENGTH" :key="i" :index="i - 1" />
                </div>
              </XhPinInputRoot>
            </XhFieldControl>
            <XhButton
              tone="brand"
              variant="outline"
              :disabled="resendSeconds > 0"
              size="lg"
              style="min-width: 132px"
              @click="handleSendCode"
            >
              <CodeCountdown v-if="resendSeconds > 0" :seconds="resendSeconds" @finish="resendSeconds = 0" />
              <template v-else>
                {{ t('page.auth.send_code') }}
              </template>
            </XhButton>
          </div>
        </XhFieldRoot>
      </XhFormFieldGroup>

      <XhFormSubmitTrigger class="auth-submit">
        {{ t('page.login.login_btn') }}
      </XhFormSubmitTrigger>
    </XhFormRoot>
  </div>
</template>
