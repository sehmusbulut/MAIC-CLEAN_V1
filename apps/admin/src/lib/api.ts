import type { AuthSession } from "./types";

export class ApiError extends Error {
  constructor(public readonly status: number, message: string, public readonly code?: string) {
    super(message);
    this.name = "ApiError";
  }
}

function normalizeSession(raw: Record<string, unknown>): AuthSession {
  const userId = typeof raw.userId === "string" && raw.userId ? raw.userId : "";
  if (!userId) throw new ApiError(401, "Oturum doğrulanamadı.");
  const ws = (raw.workspace as Record<string, unknown> | undefined) ?? {};
  return {
    userId,
    email: typeof raw.email === "string" ? raw.email : "",
    fullName: typeof raw.fullName === "string" ? raw.fullName : "Kullanıcı",
    isSuperAdmin: Boolean(raw.isSuperAdmin),
    workspace: {
      id: typeof ws.id === "string" ? ws.id : "",
      name: typeof ws.name === "string" ? ws.name : "Çalışma Alanım",
      role: (ws.role as AuthSession["workspace"]["role"]) ?? "OWNER",
      plan: (ws.plan as AuthSession["workspace"]["plan"]) ?? "STARTER",
      monthlyMessageCap: typeof ws.monthlyMessageCap === "number" ? ws.monthlyMessageCap : 5000,
      usedMessagesThisMonth: typeof ws.usedMessagesThisMonth === "number" ? ws.usedMessagesThisMonth : 0,
      storageLimitBytes: typeof ws.storageLimitBytes === "number" ? ws.storageLimitBytes : 104857600,
      usedStorageBytes: typeof ws.usedStorageBytes === "number" ? ws.usedStorageBytes : 0,
      primaryColor: typeof ws.primaryColor === "string" ? ws.primaryColor : "#7c3aed",
      widgetTitle: typeof ws.widgetTitle === "string" ? ws.widgetTitle : "Canlı Destek",
      logoUrl: typeof ws.logoUrl === "string" ? ws.logoUrl : null,
      usedPlaygroundMessagesThisMonth: typeof ws.usedPlaygroundMessagesThisMonth === "number" ? ws.usedPlaygroundMessagesThisMonth : 0,
      playgroundMonthlyCap: typeof ws.playgroundMonthlyCap === "number" ? ws.playgroundMonthlyCap : 200,
    },
    permissions: Array.isArray(raw.permissions) ? (raw.permissions as string[]) : [],
    planFeatures: Array.isArray(raw.planFeatures) ? (raw.planFeatures as string[]) : [],
  };
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const method = (options.method ?? "GET").toUpperCase();
  const isMutation = !["GET", "HEAD", "OPTIONS"].includes(method);
  const isAuthPublic = path === "/auth/login" || path === "/auth/register";

  const headers = new Headers(options.headers);
  headers.set("Accept", "application/json");

  if (isMutation && !isAuthPublic) {
    const csrfRes = await fetch("/auth/csrf", { credentials: "same-origin", cache: "no-store" });
    if (csrfRes.ok) {
      const csrfData = (await csrfRes.json()) as { token?: string };
      if (csrfData.token) headers.set("X-CSRF-TOKEN", csrfData.token);
    }
  }

  if (typeof options.body === "string" && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  const response = await fetch(path, {
    ...options,
    method,
    headers,
    credentials: "same-origin",
    cache: "no-store",
  });

  if (!response.ok) {
    let errMsg = response.status === 401 ? "E-posta veya şifre hatalı." : "İşlem sırasında bir hata oluştu.";
    let errCode: string | undefined;
    try {
      const errJson = (await response.json()) as { error?: string; message?: string; code?: string };
      errMsg = errJson.error ?? errJson.message ?? errMsg;
      errCode = errJson.code;
    } catch {
      // ignore json parse error
    }
    throw new ApiError(response.status, errMsg, errCode);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export async function apiUpload<T>(path: string, file: File): Promise<T> {
  const formData = new FormData();
  formData.append("file", file);
  return api<T>(path, { method: "POST", body: formData });
}

export async function discoverApiSession(): Promise<AuthSession> {
  const raw = await api<Record<string, unknown>>("/auth/me");
  return normalizeSession(raw);
}

export async function loginWithPassword(email: string, password: string): Promise<AuthSession> {
  const raw = await api<Record<string, unknown>>("/auth/login", {
    method: "POST",
    body: JSON.stringify({ email: email.trim(), password }),
  });
  return normalizeSession(raw);
}

export async function registerWithPassword(
  fullName: string,
  workspaceName: string,
  email: string,
  password: string
): Promise<AuthSession> {
  const raw = await api<Record<string, unknown>>("/auth/register", {
    method: "POST",
    body: JSON.stringify({
      fullName: fullName.trim(),
      workspaceName: workspaceName.trim(),
      email: email.trim(),
      password,
    }),
  });
  return normalizeSession(raw);
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message;
  if (error instanceof Error) return error.message;
  return "Sunucuya bağlanılamadı.";
}
