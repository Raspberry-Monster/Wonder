export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public fields: Record<string, string[]> = {},
  ) {
    super(message);
  }
}
export async function api<T = any>(
  path: string,
  method = "GET",
  body?: unknown,
  headers?: Record<string, string>,
): Promise<T> {
  const response = await fetch("/api/v1" + path, {
    method,
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-Wonder": "1", ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) {
    if (response.status === 401)
      window.dispatchEvent(new Event("wonder:unauthorized"));
    throw new ApiError(
      response.status,
      data?.error ||
        data?.title ||
        (
          {
            401: "请先登录",
            403: "没有操作权限",
            409: "状态已变化，请刷新后重试",
            429: "请求较多，请稍后重试",
          } as Record<number, string>
        )[response.status] ||
        `请求失败 (${response.status})`,
      data?.errors || {},
    );
  }
  return data as T;
}
export function fieldError(error: unknown, field: string): string | undefined {
  if (!(error instanceof ApiError)) return;
  return (
    Object.entries(error.fields)
      .filter(
        ([key]) => key.split(".").at(-1)?.toLowerCase() === field.toLowerCase(),
      )
      .flatMap(([, value]) => value)
      .join("；") || undefined
  );
}
