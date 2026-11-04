"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  ArrowRight,
  BookOpen,
  Code2,
  CreditCard,
  Eye,
  EyeOff,
  LayoutDashboard,
  LogOut,
  MessageSquare,
  Share2,
  ShieldCheck,
  Sliders,
  Sparkles,
  Users,
  Zap,
  type LucideIcon,
} from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import {
  api,
  discoverApiSession,
  errorMessage,
  loginWithPassword,
  registerWithPassword,
} from "@/lib/api";
import type { AuthSession, PanelProps, Section } from "@/lib/types";
import { ConversationsPanel } from "./conversations-panel";
import { KnowledgePanel } from "./knowledge-panel";
import { SettingsPanel } from "./settings-panel";
import { WidgetPanel } from "./widget-panel";
import { OverviewPanel } from "./overview-panel";
import { MembersPanel } from "./members-panel";
import {
  ChannelsPanel,
  BillingPanel,
  SuperAdminPanel,
} from "./channels-billing-superadmin";

type AuthMode = "login" | "register";

interface MenuItem {
  id: Section;
  label: string;
  icon: LucideIcon;
  description: string;
}

const MENU_ITEMS: MenuItem[] = [
  {
    id: "overview",
    label: "Genel Bakış",
    icon: LayoutDashboard,
    description: "Çalışma alanınızın anlık performans ve kullanım özeti.",
  },
  {
    id: "conversations",
    label: "Sohbetler",
    icon: MessageSquare,
    description: "Ziyaretçilerinizle gerçekleşen tüm yapay zeka ve canlı destek görüşmeleri.",
  },
  {
    id: "channels",
    label: "Kanallar",
    icon: Share2,
    description: "Web widget ve sosyal medya iletişim kanallarınızın bağlantı yönetimi.",
  },
  {
    id: "members",
    label: "Ekip",
    icon: Users,
    description: "Çalışma alanınızdaki ekip üyeleri ve rol bazlı erişim yetkileri.",
  },
  {
    id: "knowledge",
    label: "Bilgi Bankası",
    icon: BookOpen,
    description: "Yapay zeka asistanınızı eğittiğiniz dökümanlar, linkler ve davranış kuralları.",
  },
  {
    id: "billing",
    label: "Faturalama",
    icon: CreditCard,
    description: "Aktif abonelik paketiniz, mesaj limitleriniz ve fatura geçmişiniz.",
  },
  {
    id: "settings",
    label: "Ayarlar",
    icon: Sliders,
    description: "4 adımlı widget kurulum sihirbazı ve canlı Chat Playground test ortamı.",
  },
  {
    id: "widget",
    label: "Widget",
    icon: Code2,
    description: "Web sitenize ekleyeceğiniz gömülü script kodu ve doğrudan paylaşım bağlantısı.",
  },
];

const AUTH_FEATURES: { icon: LucideIcon; title: string; subtitle: string }[] = [
  {
    icon: Zap,
    title: "5 dakikada kurulum",
    subtitle: "Hızlı ve kolay entegrasyon",
  },
  {
    icon: ShieldCheck,
    title: "Kredi kartı gerekmez",
    subtitle: "Ücretsiz deneme başlatın",
  },
  {
    icon: Sparkles,
    title: "7 gün ücretsiz deneme",
    subtitle: "Tüm özellikleri keşfedin",
  },
];

function getInitials(fullName: string): string {
  const parts = fullName.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "DB";
  return parts
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? "")
    .join("");
}

interface AuthScreenProps {
  initialMessage?: string;
  onAuthenticated: (session: AuthSession) => void;
}

function AuthScreen({ initialMessage = "", onAuthenticated }: AuthScreenProps) {
  const [mode, setMode] = useState<AuthMode>("login");
  const [fullName, setFullName] = useState("");
  const [workspaceName, setWorkspaceName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);

  const [prevInitialMessage, setPrevInitialMessage] = useState(initialMessage);
  const [banner, setBanner] = useState<{ text: string; variant: "error" | "info" } | null>(() =>
    initialMessage ? { text: initialMessage, variant: "info" } : null
  );

  if (prevInitialMessage !== initialMessage) {
    setPrevInitialMessage(initialMessage);
    setBanner(initialMessage ? { text: initialMessage, variant: "info" } : null);
  }

  useEffect(() => {
    if (!banner) return;
    const timer = setTimeout(() => {
      setBanner(null);
    }, 4500);
    return () => clearTimeout(timer);
  }, [banner]);

  function handleSwitchMode(nextMode: AuthMode) {
    if (busy) return;
    setMode(nextMode);
    setBanner(null);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;

    if (mode === "register" && password.length < 8) {
      setBanner({ text: "Şifre en az 8 karakter olmalıdır.", variant: "error" });
      return;
    }

    setBusy(true);
    setBanner(null);

    try {
      const session =
        mode === "login"
          ? await loginWithPassword(email, password)
          : await registerWithPassword(fullName, workspaceName, email, password);
      onAuthenticated(session);
    } catch (err) {
      setBanner({ text: errorMessage(err), variant: "error" });
      setBusy(false);
    }
  }

  return (
    <div className="min-h-screen w-full grid grid-cols-1 lg:grid-cols-2">
      {/* A) SOL SÜTUN */}
      <section className="hidden lg:flex flex-col justify-between p-12 bg-[#7c3aed] text-white relative overflow-hidden">
        <div
          aria-hidden="true"
          className="pointer-events-none absolute -top-28 -right-28 h-96 w-96 rounded-full bg-white/10 blur-3xl"
        />
        <div
          aria-hidden="true"
          className="pointer-events-none absolute -bottom-32 -left-24 h-96 w-96 rounded-full bg-violet-900/30 blur-3xl"
        />

        {/* Sol üst marka logosu */}
        <div className="relative z-10 flex items-center gap-2.5">
          <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-white text-[#7c3aed] font-bold text-lg shadow-sm">
            D
          </div>
          <span className="text-xl font-bold tracking-tight text-white">Daribase</span>
        </div>

        {/* Orta içerik */}
        <div className="relative z-10 max-w-md mx-auto my-auto flex flex-col items-center text-center space-y-6">
          <div className="w-14 h-14 rounded-2xl bg-white/15 backdrop-blur-md flex items-center justify-center border border-white/20 shadow-lg">
            <Sparkles className="h-7 w-7 text-white" />
          </div>

          <h1 className="text-3xl font-bold tracking-tight leading-tight">
            Müşteri Desteğinizi Yapay Zeka ile Ölçekleyin
          </h1>

          <p className="text-violet-100 text-sm leading-relaxed">
            7/24 çalışan, kendi verilerinizle eğitilmiş akıllı asistanınız ile satışlarınızı artırın.
          </p>

          <div className="w-full space-y-3 pt-2">
            {AUTH_FEATURES.map((feature) => {
              const Icon = feature.icon;
              return (
                <div
                  key={feature.title}
                  className="w-full flex items-center gap-4 p-4 rounded-xl bg-white/10 backdrop-blur-sm border border-white/15 text-left"
                >
                  <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-white/15 text-white">
                    <Icon className="h-5 w-5" />
                  </div>
                  <div>
                    <div className="text-sm font-semibold text-white">{feature.title}</div>
                    <div className="text-xs text-violet-100">{feature.subtitle}</div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        {/* Alt telif ve linkler */}
        <div className="relative z-10 flex items-center justify-between text-xs text-violet-200">
          <span>© 2026 Daribase Inc.</span>
          <span className="space-x-4">
            <span>Privacy</span>
            <span>Terms</span>
          </span>
        </div>
      </section>

      {/* B) SAĞ SÜTUN */}
      <section className="flex flex-col items-center justify-center p-6 sm:p-12 bg-white relative">
        <div className="w-full max-w-[400px] space-y-6">
          {/* Mobil için üst logo */}
          <div className="flex items-center justify-center gap-2 lg:hidden">
            <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-[#7c3aed] text-white font-bold text-lg">
              D
            </div>
            <span className="text-xl font-bold tracking-tight text-slate-900">Daribase</span>
          </div>

          {/* Başlık */}
          <div className="text-center space-y-1.5">
            <h2 className="text-2xl font-bold text-slate-900">
              {mode === "login" ? "Hoş Geldiniz" : "Hesap Oluşturun"}
            </h2>
            <p className="text-sm text-slate-500">
              {mode === "login"
                ? "Hesabınıza erişmek için bilgilerinizi girin"
                : "5 dakikada kendi yapay zeka asistanınızı kurun"}
            </p>
          </div>

          {/* Form */}
          <form className="space-y-4" onSubmit={(e) => void handleSubmit(e)}>
            {mode === "register" && (
              <>
                <div className="space-y-1.5">
                  <Label htmlFor="register-fullname">Ad Soyad</Label>
                  <Input
                    id="register-fullname"
                    className="h-11 bg-slate-50/70"
                    placeholder="Adınız Soyadınız"
                    autoComplete="name"
                    value={fullName}
                    onChange={(e) => setFullName(e.target.value)}
                    required
                  />
                </div>

                <div className="space-y-1.5">
                  <Label htmlFor="register-workspace">Şirket / Çalışma Alanı Adı</Label>
                  <Input
                    id="register-workspace"
                    className="h-11 bg-slate-50/70"
                    placeholder="Örn: Fikirzen Ajans"
                    autoComplete="organization"
                    value={workspaceName}
                    onChange={(e) => setWorkspaceName(e.target.value)}
                    required
                  />
                </div>
              </>
            )}

            <div className="space-y-1.5">
              <Label htmlFor="auth-email">E-posta adresi</Label>
              <Input
                id="auth-email"
                type="email"
                className="h-11 bg-slate-50/70"
                placeholder="ornek@sirket.com"
                autoComplete="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
              />
            </div>

            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <Label htmlFor="auth-password">Şifre</Label>
                {mode === "login" && (
                  <button
                    type="button"
                    onClick={() =>
                      setBanner({
                        text: "Şifre sıfırlama bağlantısı için yöneticinizle iletişime geçebilirsiniz.",
                        variant: "info",
                      })
                    }
                    className="text-xs font-medium text-violet-600 hover:text-violet-700 hover:underline"
                  >
                    Şifremi unuttum
                  </button>
                )}
              </div>
              <div className="relative">
                <Input
                  id="auth-password"
                  type={showPassword ? "text" : "password"}
                  className="h-11 bg-slate-50/70 pr-10"
                  placeholder={mode === "register" ? "En az 8 karakter" : "••••••••"}
                  autoComplete={mode === "login" ? "current-password" : "new-password"}
                  minLength={mode === "register" ? 8 : undefined}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((prev) => !prev)}
                  className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600"
                  aria-label={showPassword ? "Şifreyi gizle" : "Şifreyi göster"}
                >
                  {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
              </div>
            </div>

            <Button
              type="submit"
              disabled={busy}
              className="w-full h-11 bg-[#7c3aed] hover:bg-[#6d28d9] text-white font-medium rounded-xl shadow-sm"
            >
              <span>
                {busy
                  ? mode === "login"
                    ? "Giriş yapılıyor…"
                    : "Hesap oluşturuluyor…"
                  : mode === "login"
                    ? "Giriş Yap"
                    : "Ücretsiz Kayıt Ol"}
              </span>
              {!busy && <ArrowRight className="h-4 w-4" />}
            </Button>

            {/* Butonun hemen altında çıkıp 4.5 sn sonra kaybolan bildirim / hata kutusu */}
            {banner && (
              <Alert
                variant={banner.variant === "error" ? "destructive" : "default"}
                className={
                  banner.variant === "error"
                    ? "bg-rose-50 border border-rose-200 text-rose-700 text-sm p-3 rounded-xl transition-all"
                    : "bg-violet-50 border border-violet-200 text-violet-800 text-sm p-3 rounded-xl transition-all"
                }
              >
                <AlertDescription className="text-center font-medium">
                  {banner.text}
                </AlertDescription>
              </Alert>
            )}
          </form>

          {/* Ayırıcı çizgi */}
          <div className="relative flex items-center justify-center">
            <div className="w-full border-t border-slate-200" />
            <span className="bg-white px-3 text-[11px] font-medium uppercase tracking-wider text-slate-400 whitespace-nowrap">
              VEYA ŞUNUNLA DEVAM ET
            </span>
            <div className="w-full border-t border-slate-200" />
          </div>

          <Button
            type="button"
            variant="outline"
            disabled
            className="w-full h-11 rounded-xl border-slate-200 text-slate-500"
          >
            Github ile Giriş (Yakında)
          </Button>

          {/* Mod değiştirme metni */}
          <p className="text-center text-sm text-slate-600">
            {mode === "login" ? (
              <>
                Hesabınız yok mu?{" "}
                <button
                  type="button"
                  onClick={() => handleSwitchMode("register")}
                  className="font-semibold text-violet-600 hover:underline"
                >
                  Ücretsiz kayıt olun
                </button>
              </>
            ) : (
              <>
                Zaten hesabınız var mı?{" "}
                <button
                  type="button"
                  onClick={() => handleSwitchMode("login")}
                  className="font-semibold text-violet-600 hover:underline"
                >
                  Giriş yapın
                </button>
              </>
            )}
          </p>
        </div>
      </section>
    </div>
  );
}

interface DashboardShellProps {
  session: AuthSession;
  onSignOut: () => void;
  onSessionRefresh: () => void;
}

function DashboardShell({ session, onSignOut, onSessionRefresh }: DashboardShellProps) {
  const [section, setSection] = useState<Section>("overview");

  const activeMenuItem =
    section === "superadmin"
      ? {
          id: "superadmin" as Section,
          label: "SuperAdmin",
          icon: ShieldCheck,
          description: "Zero-Trust SuperAdmin sistem ve paket yönetimi.",
        }
      : MENU_ITEMS.find((item) => item.id === section) ?? MENU_ITEMS[0];
  const usedMessages = session.workspace.usedMessagesThisMonth;
  const maxMessages = Math.max(1, session.workspace.monthlyMessageCap);
  const messagePercentage = Math.min(100, Math.round((usedMessages / maxMessages) * 100));

  const panelProps: PanelProps = {
    workspaceId: session.workspace.id,
    userId: session.userId,
    role: session.workspace.role,
    session,
    onSessionRefresh,
  };

  return (
    <div className="flex min-h-screen w-full bg-slate-50 text-slate-900">
      {/* Sol Sabit Sidebar */}
      <aside className="w-[250px] bg-white border-r border-slate-200 flex flex-col justify-between h-screen sticky top-0 shrink-0">
        <div className="space-y-5 py-5">
          {/* Marka Başlığı */}
          <div className="flex items-center gap-2.5 px-5">
            <div className="flex h-8 w-8 items-center justify-center rounded-xl bg-[#7c3aed] text-white font-bold text-base shadow-xs">
              D
            </div>
            <div className="min-w-0">
              <div className="text-base font-bold tracking-tight text-slate-900 leading-none">
                Daribase
              </div>
              <div className="truncate text-xs text-slate-500 mt-1">
                {session.workspace.name}
              </div>
            </div>
          </div>

          {/* Menü Listesi */}
          <div className="space-y-2">
            <div className="px-5 text-[11px] font-semibold uppercase tracking-wider text-slate-400">
              MENU
            </div>
            <nav className="space-y-1 px-3" aria-label="Ana Menü">
              {MENU_ITEMS.map((item) => {
                const Icon = item.icon;
                const isActive = item.id === section;
                return (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => setSection(item.id)}
                    className={`w-full flex items-center gap-3 px-3 py-2.5 text-sm transition-all ${
                      isActive
                        ? "bg-slate-100/80 text-slate-900 font-semibold border border-slate-200/80 rounded-xl"
                        : "text-slate-600 hover:bg-slate-50 hover:text-slate-900 rounded-xl border border-transparent"
                    }`}
                  >
                    <Icon
                      className={`h-4 w-4 shrink-0 ${
                        isActive ? "text-[#7c3aed]" : "text-slate-500"
                      }`}
                    />
                    <span className="truncate">{item.label}</span>
                  </button>
                );
              })}

              {session.isSuperAdmin && (
                <button
                  type="button"
                  onClick={() => setSection("superadmin")}
                  className={`w-full flex items-center gap-3 px-3 py-2.5 text-sm transition-all mt-2 ${
                    section === "superadmin"
                      ? "bg-slate-900 text-white font-semibold rounded-xl shadow-xs"
                      : "text-slate-700 hover:bg-slate-100 rounded-xl border border-slate-200/80"
                  }`}
                >
                  <ShieldCheck
                    className={`h-4 w-4 shrink-0 ${
                      section === "superadmin"
                        ? "text-violet-300"
                        : "text-[#7c3aed]"
                    }`}
                  />
                  <span className="truncate">SuperAdmin</span>
                </button>
              )}
            </nav>
          </div>
        </div>

        {/* Alt Mesaj Hakkı Kotası Kartı */}
        <div className="p-4">
          <div className="rounded-2xl border border-slate-200 bg-slate-50/70 p-4 space-y-2.5">
            <div className="flex items-center justify-between text-xs">
              <span className="font-semibold text-slate-700">Mesaj hakkı</span>
              <span className="font-medium text-slate-600">
                {usedMessages} / {session.workspace.monthlyMessageCap}
              </span>
            </div>
            <Progress value={messagePercentage} />
            <div className="flex items-center justify-between text-[11px] text-slate-500">
              <span>Aylık sıfırlanır</span>
              <span>%{messagePercentage}</span>
            </div>
          </div>
        </div>
      </aside>

      {/* Sağ Ana Sütun */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* Üst Bar */}
        <header className="h-14 bg-white border-b border-slate-200 px-6 flex items-center justify-between sticky top-0 z-20">
          {/* Sol Breadcrumb */}
          <div className="flex items-center gap-2 text-sm min-w-0">
            <div className="flex h-6 w-6 items-center justify-center rounded-md bg-[#7c3aed] text-white text-xs font-bold shrink-0">
              D
            </div>
            <span className="font-semibold text-slate-900 hidden sm:inline">Daribase</span>
            <span className="text-slate-300 hidden sm:inline">/</span>
            <span className="font-medium text-slate-700 truncate max-w-[160px]">
              {session.workspace.name}
            </span>
            <Badge variant="outline" className="text-[11px] uppercase">
              {session.workspace.plan}
            </Badge>
            <span className="text-slate-300">/</span>
            <span className="font-semibold text-slate-900 truncate">{activeMenuItem.label}</span>
            <Badge variant="secondary" className="text-[11px] bg-violet-50 text-violet-700">
              {session.workspace.role}
            </Badge>
          </div>

          {/* Sağ Kullanıcı ve Aksiyonlar */}
          <div className="flex items-center gap-3">
            <Badge variant="outline" className="px-2.5 py-0.5 text-xs font-semibold text-slate-700">
              TR
            </Badge>
            <div className="flex items-center gap-2">
              <Avatar className="h-8 w-8">
                <AvatarFallback>{getInitials(session.fullName)}</AvatarFallback>
              </Avatar>
              <span className="hidden md:inline text-xs font-medium text-slate-700 max-w-[140px] truncate">
                {session.fullName}
              </span>
            </div>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={onSignOut}
              className="text-slate-600 hover:text-rose-600 gap-1.5"
            >
              <LogOut className="h-4 w-4" />
              <span className="hidden sm:inline">Çıkış</span>
            </Button>
          </div>
        </header>

        {/* İçerik Alanı */}
        <main className="flex-1 min-w-0">
          {section === "overview" ? (
            <OverviewPanel {...panelProps} onNavigate={setSection} />
          ) : section === "conversations" ? (
            <ConversationsPanel {...panelProps} />
          ) : section === "channels" ? (
            <ChannelsPanel {...panelProps} onNavigate={setSection} />
          ) : section === "members" ? (
            <MembersPanel {...panelProps} />
          ) : section === "knowledge" ? (
            <KnowledgePanel {...panelProps} />
          ) : section === "billing" ? (
            <BillingPanel {...panelProps} />
          ) : section === "settings" ? (
            <SettingsPanel {...panelProps} />
          ) : section === "widget" ? (
            <WidgetPanel {...panelProps} />
          ) : section === "superadmin" && session.isSuperAdmin ? (
            <SuperAdminPanel session={session} />
          ) : (
            <OverviewPanel {...panelProps} onNavigate={setSection} />
          )}
        </main>
      </div>
    </div>
  );
}

export function AdminConsole() {
  const [status, setStatus] = useState<"loading" | "anonymous" | "authenticated">("loading");
  const [session, setSession] = useState<AuthSession | null>(null);
  const [authMessage, setAuthMessage] = useState("");

  const refreshSession = useCallback(async () => {
    try {
      const current = await discoverApiSession();
      setSession(current);
      setStatus("authenticated");
    } catch {
      setSession(null);
      setStatus("anonymous");
    }
  }, []);

  useEffect(() => {
    let active = true;
    discoverApiSession()
      .then((current) => {
        if (!active) return;
        setSession(current);
        setStatus("authenticated");
      })
      .catch(() => {
        if (!active) return;
        setSession(null);
        setStatus("anonymous");
      });
    return () => {
      active = false;
    };
  }, []);

  async function handleSignOut() {
    try {
      await api<void>("/auth/logout", { method: "POST" });
    } catch {
      // ignore logout error and clear local state
    }
    setSession(null);
    setAuthMessage("Çıkış yaptınız.");
    setStatus("anonymous");
  }

  if (status === "loading") {
    return (
      <div className="min-h-screen w-full flex items-center justify-center bg-slate-50">
        <div className="flex items-center gap-3 text-sm font-medium text-slate-600">
          <div className="h-5 w-5 animate-spin rounded-full border-2 border-[#7c3aed] border-t-transparent" />
          <span>Daribase yükleniyor…</span>
        </div>
      </div>
    );
  }

  if (status === "anonymous" || !session) {
    return (
      <AuthScreen
        initialMessage={authMessage}
        onAuthenticated={(nextSession) => {
          setAuthMessage("");
          setSession(nextSession);
          setStatus("authenticated");
        }}
      />
    );
  }

  return (
    <DashboardShell
      session={session}
      onSignOut={() => void handleSignOut()}
      onSessionRefresh={() => void refreshSession()}
    />
  );
}
