export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly detail?: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
}

async function parseBody(response: Response): Promise<unknown> {
  if (response.status === 204) {
    return null
  }

  const contentType = response.headers.get('content-type') ?? ''
  if (contentType.includes('application/json') || contentType.includes('application/problem+json')) {
    return response.json().catch(() => null)
  }

  return response.text().catch(() => '')
}

/** 统一请求封装：携带会话 Cookie，并把后端 ProblemDetails 归一化为 ApiError。 */
export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  const body = await parseBody(response)

  if (!response.ok) {
    const problem = (body ?? {}) as ProblemDetails
    const message = problem.title ?? `请求失败（${response.status}）`
    throw new ApiError(response.status, message, problem.detail)
  }

  return body as T
}
