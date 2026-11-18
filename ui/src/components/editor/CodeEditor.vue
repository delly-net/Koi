<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { basicSetup } from 'codemirror'
import { Compartment, EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { languageForScheme } from './languages'

const props = withDefaults(
  defineProps<{
    modelValue: string
    /** 着色方案标识；变化时仅重建语言扩展，不重置文档与光标。 */
    scheme?: string
    /** 所在工作区页签是否可见；隐藏页签尺寸为 0，激活时重新测量。 */
    active?: boolean
  }>(),
  {
    scheme: 'plaintext',
    active: true,
  },
)

const emit = defineEmits<{
  'update:modelValue': [value: string]
  save: []
}>()

const host = ref<HTMLDivElement | null>(null)
const languageCompartment = new Compartment()

let view: EditorView | null = null
let resizeObserver: ResizeObserver | null = null

/** 与原先 textarea 保持一致的等宽字体、行高与内边距。 */
const theme = EditorView.theme({
  '&': { height: '100%', fontSize: '13px' },
  '&.cm-focused': { outline: 'none' },
  '.cm-scroller': {
    fontFamily: "ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace",
    lineHeight: '1.6',
    overflow: 'auto',
  },
  '.cm-content': { padding: '10px 0' },
  '.cm-gutters': {
    backgroundColor: '#fafafa',
    borderRight: '1px solid #e4e7ed',
    color: '#a8abb2',
  },
  '.cm-activeLineGutter': { backgroundColor: '#f0f2f5' },
})

onMounted(() => {
  if (!host.value) {
    return
  }

  view = new EditorView({
    state: EditorState.create({
      doc: props.modelValue,
      extensions: [
        basicSetup,
        // Ctrl/Cmd+S 交给页面保存；阻止浏览器默认的“保存网页”。
        keymap.of([
          {
            key: 'Mod-s',
            preventDefault: true,
            run: () => {
              emit('save')
              return true
            },
          },
        ]),
        languageCompartment.of(languageForScheme(props.scheme)),
        theme,
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            emit('update:modelValue', update.state.doc.toString())
          }
        }),
      ],
    }),
    parent: host.value,
  })

  resizeObserver = new ResizeObserver(() => view?.requestMeasure())
  resizeObserver.observe(host.value)
})

// 外部内容变化（重新加载/保存后回写）才整体替换文档；输入时父子值相同，不会触发。
watch(
  () => props.modelValue,
  (value) => {
    if (!view || view.state.doc.toString() === value) {
      return
    }

    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: value },
    })
  },
)

watch(
  () => props.scheme,
  (scheme) => {
    view?.dispatch({
      effects: languageCompartment.reconfigure(languageForScheme(scheme)),
    })
  },
)

// el-tabs 用 display:none 隐藏非激活面板，隐藏期间尺寸为 0；激活时重新测量。
watch(
  () => props.active,
  (active) => {
    if (active) {
      requestAnimationFrame(() => view?.requestMeasure())
    }
  },
)

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
  view?.destroy()
  view = null
})
</script>

<template>
  <div ref="host" class="code-editor"></div>
</template>

<style scoped>
.code-editor {
  height: 100%;
  overflow: hidden;
}
</style>
