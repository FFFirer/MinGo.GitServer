import createClient from "openapi-fetch";
import type { paths } from "./schema";

export const api = createClient<paths>({ baseUrl: location.origin });
let token = "";
api.use({
  onRequest({ request }) {
    if (request.method !== "GET") request.headers.set("X-CSRF-Token", token);
    return request;
  },
});

export class ApiError extends Error {
  constructor(readonly status: number, message: string) { super(message); }
}

export async function result<T>(operation: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await operation;
  if (!response.ok) {
    const detail = error && typeof error === "object" && "detail" in error ? String(error.detail) : "";
    throw new ApiError(response.status, detail || `请求失败（HTTP ${response.status}）`);
  }
  return data as T;
}

export async function session() {
  const state = await result(api.GET("/api/v1/session"));
  token = state.csrfToken;
  return state;
}
