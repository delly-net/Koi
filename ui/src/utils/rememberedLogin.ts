/**
 * 登录页「记住用户名 / 记住密码」的本地存储工具。
 *
 * 安全边界：
 * - 浏览器 localStorage 没有真正的秘密；同源脚本、浏览器扩展或拥有本机
 *   文件访问权的用户都可能读取或解密，本工具属于「防肉眼直读 + 防简单复制」
 *   级别，不能防御恶意同源代码（如 XSS）。
 * - 不保存明文密码，不向服务端发送密文或明文，日志中不输出密码。
 *
 * 加密方式：
 * - 安全上下文（HTTPS / localhost）下使用 Web Crypto AES-GCM，密钥由
 *   PBKDF2-SHA256 从应用内置口令与每条记录随机生成的盐派生。
 * - 纯 HTTP 等非安全上下文没有 crypto.subtle，回退为固定口令 XOR + Base64
 *   混淆（alg = "xor"），仅保证功能可用，安全性更低。
 */

const LAST_USER_KEY = 'koi.login.lastUserName'
const PASSWORD_KEY = 'koi.login.rememberedPassword'

/** 存储格式版本；未知版本按损坏记录清除，避免阻塞登录页。 */
const RECORD_VERSION = 1
/** 应用内置口令（非用户秘密）。 */
const APP_PASSPHRASE = 'koi.linux.web.ui:remembered-password:v1'
const PBKDF2_ITERATIONS = 210_000
const PBKDF2_HASH = 'SHA-256'
const AES_KEY_BITS = 256
const AES_GCM_TAG_BITS = 128
const SALT_BYTES = 16
const IV_BYTES = 12

type CipherAlgorithm = 'AES-GCM' | 'xor'

interface RememberedPasswordRecord {
  v: number
  alg: CipherAlgorithm
  salt?: string
  iv?: string
  data: string
}

const textEncoder = new TextEncoder()
const textDecoder = new TextDecoder()

/** 访问 localStorage；隐私模式或禁用存储时浏览器可能直接抛异常。 */
function getStorage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

/** 仅在安全上下文（HTTPS / localhost）中返回 SubtleCrypto。 */
function getSubtle(): SubtleCrypto | null {
  if (typeof window === 'undefined' || !window.isSecureContext) {
    return null
  }
  return window.crypto?.subtle ?? null
}

/** 字节数组转 Base64；密码长度有限，逐字节拼接足够。 */
function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) {
    binary += String.fromCharCode(byte)
  }
  return btoa(binary)
}

/** Base64 转字节数组，内容非法时抛出异常由调用方按损坏记录处理。 */
function base64ToBytes(value: string): Uint8Array<ArrayBuffer> {
  const binary = atob(value)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index += 1) {
    bytes[index] = binary.charCodeAt(index)
  }
  return bytes
}

/** 用内置口令派生 AES-GCM 密钥。 */
async function deriveKey(
  subtle: SubtleCrypto,
  salt: Uint8Array<ArrayBuffer>,
): Promise<CryptoKey> {
  const baseKey = await subtle.importKey(
    'raw',
    textEncoder.encode(APP_PASSPHRASE),
    'PBKDF2',
    false,
    ['deriveKey'],
  )

  return subtle.deriveKey(
    {
      name: 'PBKDF2',
      salt,
      iterations: PBKDF2_ITERATIONS,
      hash: PBKDF2_HASH,
    },
    baseKey,
    { name: 'AES-GCM', length: AES_KEY_BITS },
    false,
    ['encrypt', 'decrypt'],
  )
}

/** 固定口令 XOR 混淆（非安全上下文降级路径）。 */
function xorBytes(value: Uint8Array): Uint8Array {
  const key = textEncoder.encode(APP_PASSPHRASE)
  const result = new Uint8Array(value.length)
  for (let index = 0; index < value.length; index += 1) {
    result[index] = value[index] ^ key[index % key.length]
  }
  return result
}

function encryptWithXor(password: string): RememberedPasswordRecord {
  return {
    v: RECORD_VERSION,
    alg: 'xor',
    data: bytesToBase64(xorBytes(textEncoder.encode(password))),
  }
}

async function encryptPassword(password: string): Promise<RememberedPasswordRecord> {
  const subtle = getSubtle()
  if (!subtle) {
    return encryptWithXor(password)
  }

  try {
    const salt = window.crypto.getRandomValues(new Uint8Array(SALT_BYTES))
    const iv = window.crypto.getRandomValues(new Uint8Array(IV_BYTES))
    const key = await deriveKey(subtle, salt)
    const cipher = await subtle.encrypt(
      { name: 'AES-GCM', iv, tagLength: AES_GCM_TAG_BITS },
      key,
      textEncoder.encode(password),
    )

    return {
      v: RECORD_VERSION,
      alg: 'AES-GCM',
      salt: bytesToBase64(salt),
      iv: bytesToBase64(iv),
      data: bytesToBase64(new Uint8Array(cipher)),
    }
  } catch (error) {
    // AES 不可用时仍保留「记住密码」能力，避免整个功能失效。
    console.warn('[登录] AES-GCM 加密失败，改用本地混淆存储：', error)
    return encryptWithXor(password)
  }
}

/** 读取上次成功登录的用户名；无记录或存储不可用时返回空串。 */
export function getLastUserName(): string {
  const storage = getStorage()
  if (!storage) {
    return ''
  }

  try {
    return storage.getItem(LAST_USER_KEY) ?? ''
  } catch {
    return ''
  }
}

/** 保存上次成功登录的用户名；仅登录成功后调用。 */
export function saveLastUserName(userName: string): void {
  const name = userName.trim()
  if (!name) {
    return
  }

  const storage = getStorage()
  if (!storage) {
    return
  }

  try {
    storage.setItem(LAST_USER_KEY, name)
  } catch (error) {
    console.warn('[登录] 保存上次登录用户名失败：', error)
  }
}

/** 删除本地记住的密码密文。 */
export function clearRememberedPassword(): void {
  const storage = getStorage()
  if (!storage) {
    return
  }

  try {
    storage.removeItem(PASSWORD_KEY)
  } catch {
    // 存储不可用时无需处理。
  }
}

/**
 * 加密并保存密码。失败时仅告警，不影响登录流程。
 * 空密码视为取消记住，直接清除记录。
 */
export async function saveRememberedPassword(password: string): Promise<void> {
  if (!password) {
    clearRememberedPassword()
    return
  }

  const storage = getStorage()
  if (!storage) {
    return
  }

  try {
    const record = await encryptPassword(password)
    storage.setItem(PASSWORD_KEY, JSON.stringify(record))
  } catch (error) {
    console.warn('[登录] 保存记住密码失败：', error)
  }
}

/**
 * 读取并解密记住的密码。
 * 记录不存在返回 null；格式损坏、算法未知或解密失败时清除记录并返回 null，
 * 保证登录页不会因本地数据异常而报错。
 */
export async function loadRememberedPassword(): Promise<string | null> {
  const storage = getStorage()
  if (!storage) {
    return null
  }

  let raw: string | null
  try {
    raw = storage.getItem(PASSWORD_KEY)
  } catch {
    return null
  }
  if (!raw) {
    return null
  }

  let record: Partial<RememberedPasswordRecord>
  try {
    record = JSON.parse(raw) as Partial<RememberedPasswordRecord>
  } catch {
    clearRememberedPassword()
    return null
  }

  if (
    !record ||
    typeof record !== 'object' ||
    record.v !== RECORD_VERSION ||
    typeof record.data !== 'string'
  ) {
    clearRememberedPassword()
    return null
  }

  if (record.alg === 'AES-GCM') {
    const subtle = getSubtle()
    if (!subtle || typeof record.salt !== 'string' || typeof record.iv !== 'string') {
      clearRememberedPassword()
      return null
    }

    try {
      const salt = base64ToBytes(record.salt)
      const iv = base64ToBytes(record.iv)
      const key = await deriveKey(subtle, salt)
      const plain = await subtle.decrypt(
        { name: 'AES-GCM', iv, tagLength: AES_GCM_TAG_BITS },
        key,
        base64ToBytes(record.data),
      )
      return textDecoder.decode(plain)
    } catch {
      clearRememberedPassword()
      return null
    }
  }

  if (record.alg === 'xor') {
    try {
      return textDecoder.decode(xorBytes(base64ToBytes(record.data)))
    } catch {
      clearRememberedPassword()
      return null
    }
  }

  clearRememberedPassword()
  return null
}
