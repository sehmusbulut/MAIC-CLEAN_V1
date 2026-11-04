"use client";

import React, { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, errorMessage } from "@/lib/api";
import type {
  AuthSession,
  BillingOverviewResponse,
  PanelProps,
  PlanDefinitionItem,
  PlanTier,
  Section,
  WizardCatalogOption,
} from "@/lib/types";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertCircle,
  ArrowRight,
  Check,
  CheckCircle2,
  Code2,
  CreditCard,
  Database,
  FileText,
  Globe,
  HardDrive,
  KeyRound,
  Layers,
  Loader2,
  Lock,
  MessageCircle,
  MessageSquare,
  Plus,
  RefreshCw,
  Save,
  Share2,
  ShieldCheck,
  ShoppingBag,
  Sparkles,
  Trash2,
  Users,
} from "lucide-react";

function formatBytesToMb(bytes: number): string {
  if (!bytes || bytes <= 0) return "0 MB";
  const mb = bytes / (1024 * 1024);
  if (mb >= 1024) {
    return `${(mb / 1024).toFixed(1)} GB`;
  }
  return `${Math.round(mb)} MB`;
}

// ============================================================================
// 1. CHANNELS PANEL (Kanallar - Gerçek Omnichannel Entegrasyonları)
// ============================================================================
export { ChannelsPanel } from "@/components/channels-panel";

// ============================================================================
// 2. BILLING PANEL (Faturalama & Paket Yönetimi)
// ============================================================================
export function BillingPanel({ workspaceId, onSessionRefresh }: PanelProps) {
  const [data, setData] = useState<BillingOverviewResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [upgradingPlan, setUpgradingPlan] = useState<PlanTier | null>(null);
  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  useEffect(() => {
    if (!feedback) return;
    const t = setTimeout(() => setFeedback(null), 4000);
    return () => clearTimeout(t);
  }, [feedback]);

  const loadBilling = useCallback(async () => {
    setLoading(true);
    try {
      const res = await api<BillingOverviewResponse>(
        `/workspaces/${workspaceId}/billing`
      );
      setData(res);
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setLoading(false);
    }
  }, [workspaceId]);

  useEffect(() => {
    void loadBilling();
  }, [loadBilling]);

  const handleUpgrade = async (targetPlan: PlanTier) => {
    if (upgradingPlan) return;
    setUpgradingPlan(targetPlan);
    setFeedback(null);

    try {
      const updated = await api<BillingOverviewResponse>(
        `/workspaces/${workspaceId}/billing/upgrade`,
        {
          method: "POST",
          body: JSON.stringify({ targetPlan }),
        }
      );
      setData(updated);
      setFeedback({
        type: "success",
        text: `Çalışma alanı paketiniz başarıyla ${targetPlan} olarak güncellendi.`,
      });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setUpgradingPlan(null);
    }
  };

  if (loading && !data) {
    return (
      <div className="p-6 max-w-[1400px] mx-auto flex items-center justify-center min-h-[420px]">
        <div className="flex flex-col items-center gap-3 text-slate-500">
          <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed]" />
          <p className="text-sm font-medium">
            Faturalama ve paket bilgileri yükleniyor...
          </p>
        </div>
      </div>
    );
  }

  const usage = data?.usage ?? {
    usedMessagesThisMonth: 0,
    monthlyMessageCap: 1000,
    usedStorageBytes: 0,
    storageLimitBytes: 104857600,
    documentCount: 0,
    maxDocumentCount: 10,
    urlCount: 0,
    maxUrlSourceCount: 5,
    memberCount: 1,
    maxSeats: 1,
    usedPlaygroundMessagesThisMonth: 0,
    playgroundMonthlyCap: 200,
  };

  const msgPercent = Math.min(
    100,
    Math.round(
      (usage.usedMessagesThisMonth / Math.max(1, usage.monthlyMessageCap)) * 100
    )
  );
  const storagePercent = Math.min(
    100,
    Math.round(
      (usage.usedStorageBytes / Math.max(1, usage.storageLimitBytes)) * 100
    )
  );

  return (
    <div className="p-6 max-w-[1400px] mx-auto space-y-8">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="space-y-1">
          <div className="flex items-center gap-2.5">
            <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
              Faturalama &amp; Paketler
            </h1>
            <Badge className="bg-[#7c3aed] text-white">
              MEVCUT PLAN: {data?.currentPlan ?? "STARTER"}
            </Badge>
          </div>
          <p className="text-sm text-slate-500">
            Çalışma alanınızın anlık kota kullanımlarını takip edin ve
            ihtiyacınıza uygun pakete geçiş yapın.
          </p>
        </div>
      </div>

      {feedback && (
        <Alert
          variant={feedback.type === "error" ? "destructive" : "default"}
          className={
            feedback.type === "success"
              ? "border-emerald-200 bg-emerald-50 text-emerald-900 rounded-2xl"
              : "rounded-2xl"
          }
        >
          <div className="flex items-center gap-2">
            {feedback.type === "success" ? (
              <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
            ) : (
              <AlertCircle className="w-4 h-4 shrink-0" />
            )}
            <AlertDescription className="text-xs font-medium">
              {feedback.text}
            </AlertDescription>
          </div>
        </Alert>
      )}

      {/* Üstte Mevcut Paket ve Anlık Kota Kullanımı Kartları */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {/* 1) Aylık Mesaj Hakkı */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 space-y-3">
            <div className="flex items-center justify-between">
              <span className="text-xs font-bold uppercase tracking-wider text-slate-500">
                Aylık Mesaj Hakkı
              </span>
              <MessageSquare className="w-4 h-4 text-[#7c3aed]" />
            </div>
            <div className="flex items-baseline justify-between">
              <span className="text-2xl font-extrabold text-slate-900">
                {usage.usedMessagesThisMonth.toLocaleString("tr-TR")}
              </span>
              <span className="text-xs font-semibold text-slate-500">
                / {usage.monthlyMessageCap.toLocaleString("tr-TR")} mesaj
              </span>
            </div>
            <Progress value={msgPercent} className="h-2" />
          </CardContent>
        </Card>

        {/* 2) Bilgi Bankası Depolama */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 space-y-3">
            <div className="flex items-center justify-between">
              <span className="text-xs font-bold uppercase tracking-wider text-slate-500">
                Bilgi Bankası Depolama
              </span>
              <HardDrive className="w-4 h-4 text-[#7c3aed]" />
            </div>
            <div className="flex items-baseline justify-between">
              <span className="text-2xl font-extrabold text-slate-900">
                {formatBytesToMb(usage.usedStorageBytes)}
              </span>
              <span className="text-xs font-semibold text-slate-500">
                / {formatBytesToMb(usage.storageLimitBytes)}
              </span>
            </div>
            <Progress value={storagePercent} className="h-2" />
          </CardContent>
        </Card>

        {/* 3) Döküman, Web Link, Koltuk & Playground Özeti */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 space-y-2.5">
            <div className="flex items-center justify-between text-xs">
              <span className="text-slate-500 flex items-center gap-1.5">
                <FileText className="w-3.5 h-3.5 text-slate-400" />
                Döküman Kotası
              </span>
              <span className="font-bold text-slate-900">
                {usage.documentCount} / {usage.maxDocumentCount}
              </span>
            </div>

            <div className="flex items-center justify-between text-xs">
              <span className="text-slate-500 flex items-center gap-1.5">
                <Globe className="w-3.5 h-3.5 text-slate-400" />
                Web Link Kotası
              </span>
              <span className="font-bold text-slate-900">
                {usage.urlCount} / {usage.maxUrlSourceCount}
              </span>
            </div>

            <div className="flex items-center justify-between text-xs">
              <span className="text-slate-500 flex items-center gap-1.5">
                <Users className="w-3.5 h-3.5 text-slate-400" />
                Ekip Üyesi Koltuk Limiti
              </span>
              <span className="font-bold text-slate-900">
                {usage.memberCount} / {usage.maxSeats} Koltuk
              </span>
            </div>

            <div className="flex items-center justify-between text-xs">
              <span className="text-slate-500 flex items-center gap-1.5">
                <Sparkles className="w-3.5 h-3.5 text-slate-400" />
                Playground Test Kredisi
              </span>
              <span className="font-bold text-slate-900">
                {usage.usedPlaygroundMessagesThisMonth} /{" "}
                {usage.playgroundMonthlyCap}
              </span>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Altta 4 Paket Karşılaştırma ve Yükseltme Kartı */}
      <div className="space-y-3">
        <div>
          <h2 className="text-lg font-bold text-slate-900">
            Paket Karşılaştırma ve Yükseltme
          </h2>
          <p className="text-xs text-slate-500">
            Paket değişikliği anında uygulanır ve kotalarınız otomatik olarak
            güncellenir.
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-5">
          {(data?.plans ?? []).map((planItem) => {
            const code = (planItem.planCode ??
              planItem.plan ??
              "STARTER") as PlanTier;
            const title = planItem.displayName ?? planItem.name ?? code;
            const storageBytes =
              planItem.maxStorageBytes ?? planItem.storageLimitBytes ?? 0;
            const isCurrent = data?.currentPlan === code;
            const isBusy = upgradingPlan === code;

            return (
              <Card
                key={code}
                className={`rounded-2xl flex flex-col justify-between transition-all ${
                  isCurrent
                    ? "border-2 border-[#7c3aed] shadow-md bg-violet-50/15"
                    : "border-slate-200/80 shadow-xs"
                }`}
              >
                <CardHeader className="pb-3 space-y-2">
                  <div className="flex items-center justify-between">
                    <Badge
                      variant={isCurrent ? "default" : "secondary"}
                      className={
                        isCurrent ? "bg-[#7c3aed] text-white" : "text-xs"
                      }
                    >
                      {code}
                    </Badge>
                    {isCurrent && (
                      <span className="text-[11px] font-bold text-[#7c3aed]">
                        Aktif Paketiniz
                      </span>
                    )}
                  </div>
                  <CardTitle className="text-lg font-bold text-slate-900">
                    {title}
                  </CardTitle>
                  <div className="pt-1">
                    <span className="text-3xl font-extrabold text-slate-900">
                      {planItem.monthlyPriceTry.toLocaleString("tr-TR")} ₺
                    </span>
                    <span className="text-xs text-slate-500 font-medium">
                      {" "}
                      / ay
                    </span>
                  </div>
                </CardHeader>

                <CardContent className="space-y-4">
                  <div className="space-y-2 text-xs text-slate-700 border-t border-slate-100 pt-3">
                    <div className="flex items-center gap-2">
                      <Check className="w-4 h-4 text-emerald-600 shrink-0" />
                      <span>
                        <strong>
                          {planItem.monthlyMessageLimit.toLocaleString("tr-TR")}
                        </strong>{" "}
                        Aylık AI Mesajı
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Check className="w-4 h-4 text-emerald-600 shrink-0" />
                      <span>
                        <strong>{planItem.maxSeats}</strong> Maksimum Ekip Üyesi
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Check className="w-4 h-4 text-emerald-600 shrink-0" />
                      <span>
                        <strong>{formatBytesToMb(storageBytes)}</strong> Bilgi
                        Bankası Depolama
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Check className="w-4 h-4 text-emerald-600 shrink-0" />
                      <span>
                        <strong>{planItem.maxDocumentCount}</strong> Döküman &amp;{" "}
                        <strong>{planItem.maxUrlSourceCount}</strong> Web Linki
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <Check className="w-4 h-4 text-emerald-600 shrink-0" />
                      <span>
                        <strong>{planItem.playgroundMonthlyLimit || 200}</strong>{" "}
                        Playground Test Kredisi
                      </span>
                    </div>
                  </div>

                  <Button
                    type="button"
                    disabled={isCurrent || Boolean(upgradingPlan)}
                    onClick={() => void handleUpgrade(code)}
                    variant={isCurrent ? "outline" : "default"}
                    className={
                      isCurrent
                        ? "w-full rounded-xl h-10 text-xs font-semibold"
                        : "w-full rounded-xl h-10 text-xs font-semibold bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
                    }
                  >
                    {isBusy ? (
                      <>
                        <Loader2 className="w-4 h-4 mr-1.5 animate-spin" />
                        Güncelleniyor...
                      </>
                    ) : isCurrent ? (
                      "Mevcut Paket"
                    ) : (
                      <>
                        <CreditCard className="w-3.5 h-3.5 mr-1.5" />
                        Pakete Geç
                      </>
                    )}
                  </Button>
                </CardContent>
              </Card>
            );
          })}
        </div>
      </div>
    </div>
  );
}

// ============================================================================
// 3. SUPERADMIN PANEL (Zero-Trust Master PIN Korumalı Yönetim Paneli)
// ============================================================================
export function SuperAdminPanel({ session }: { session: AuthSession }) {
  const [masterKey, setMasterKey] = useState("");
  const [activeTab, setActiveTab] = useState<"plans" | "catalog">("plans");

  const [plans, setPlans] = useState<PlanDefinitionItem[]>([]);
  const [catalogItems, setCatalogItems] = useState<WizardCatalogOption[]>([]);
  const [loading, setLoading] = useState(true);
  const [savingPlanCode, setSavingPlanCode] = useState<string | null>(null);

  // Yeni katalog öğesi ekleme state'leri
  const [newCategory, setNewCategory] = useState<
    "USE_CASE" | "ASSISTANT_ROLE" | "PRIMARY_CHANNEL" | "KNOWLEDGE_METHOD"
  >("USE_CASE");
  const [newKey, setNewKey] = useState("");
  const [newTitle, setNewTitle] = useState("");
  const [newDescription, setNewDescription] = useState("");
  const [newIconName, setNewIconName] = useState("Sparkles");
  const [newWelcome, setNewWelcome] = useState("");
  const [addingCatalog, setAddingCatalog] = useState(false);
  const [deletingCatalogId, setDeletingCatalogId] = useState<string | null>(
    null
  );

  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  useEffect(() => {
    if (!feedback) return;
    const t = setTimeout(() => setFeedback(null), 4500);
    return () => clearTimeout(t);
  }, [feedback]);

  const loadSuperAdminData = useCallback(async () => {
    setLoading(true);
    try {
      const [plansRes, catalogRes] = await Promise.all([
        api<PlanDefinitionItem[]>("/superadmin/plans"),
        api<WizardCatalogOption[]>("/superadmin/wizard-catalog"),
      ]);
      setPlans(plansRes ?? []);
      setCatalogItems(catalogRes ?? []);
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSuperAdminData();
  }, [loadSuperAdminData]);

  const updatePlanField = (
    idx: number,
    field: keyof PlanDefinitionItem,
    value: string | number
  ) => {
    setPlans((prev) =>
      prev.map((item, i) => (i === idx ? { ...item, [field]: value } : item))
    );
  };

  const handleSavePlan = async (planItem: PlanDefinitionItem) => {
    const code = (planItem.planCode ?? planItem.plan ?? "STARTER") as PlanTier;
    if (!masterKey.trim()) {
      setFeedback({
        type: "error",
        text: "Değişiklik yapmak için üstteki SuperAdmin Master Anahtarı (X-SuperAdmin-Key) alanını doldurmalısınız.",
      });
      return;
    }

    setSavingPlanCode(code);
    setFeedback(null);

    try {
      const storageBytes =
        planItem.maxStorageBytes ?? planItem.storageLimitBytes ?? 104857600;
      const updated = await api<PlanDefinitionItem>(
        `/superadmin/plans/${code}`,
        {
          method: "PUT",
          headers: {
            "X-SuperAdmin-Key": masterKey.trim(),
          },
          body: JSON.stringify({
            name: planItem.displayName ?? planItem.name ?? code,
            monthlyPriceTry: Number(planItem.monthlyPriceTry),
            monthlyMessageLimit: Number(planItem.monthlyMessageLimit),
            maxSeats: Number(planItem.maxSeats),
            storageLimitBytes: Number(storageBytes),
            maxStorageBytes: Number(storageBytes),
            maxDocumentCount: Number(planItem.maxDocumentCount),
            maxUrlSourceCount: Number(planItem.maxUrlSourceCount),
            playgroundMonthlyLimit: Number(
              planItem.playgroundMonthlyLimit || 200
            ),
          }),
        }
      );

      setPlans((prev) =>
        prev.map((p) =>
          (p.planCode ?? p.plan) === code ? { ...p, ...updated } : p
        )
      );
      setFeedback({
        type: "success",
        text: `${code} paket limitleri ve fiyatı canlı olarak güncellendi.`,
      });
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setSavingPlanCode(null);
    }
  };

  const handleAddCatalogItem = async (e: FormEvent) => {
    e.preventDefault();
    if (!masterKey.trim()) {
      setFeedback({
        type: "error",
        text: "Katalog öğesi eklemek için SuperAdmin Master Anahtarı (X-SuperAdmin-Key) zorunludur.",
      });
      return;
    }
    if (!newKey.trim() || !newTitle.trim() || !newDescription.trim()) {
      setFeedback({
        type: "error",
        text: "Anahtar (key), başlık ve açıklama alanları zorunludur.",
      });
      return;
    }

    setAddingCatalog(true);
    setFeedback(null);

    try {
      const created = await api<WizardCatalogOption>(
        "/superadmin/wizard-catalog",
        {
          method: "POST",
          headers: {
            "X-SuperAdmin-Key": masterKey.trim(),
          },
          body: JSON.stringify({
            category: newCategory,
            key: newKey.trim().toLowerCase(),
            title: newTitle.trim(),
            description: newDescription.trim(),
            iconName: newIconName.trim() || "Sparkles",
            defaultWelcomeMessage: newWelcome.trim() || null,
            isActive: true,
          }),
        }
      );

      setCatalogItems((prev) => [...prev, created]);
      setNewKey("");
      setNewTitle("");
      setNewDescription("");
      setNewWelcome("");
      setFeedback({
        type: "success",
        text: "Yeni sihirbaz katalog seçeneği başarıyla eklendi.",
      });
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setAddingCatalog(false);
    }
  };

  const handleDeleteCatalogItem = async (id: string) => {
    if (!masterKey.trim()) {
      setFeedback({
        type: "error",
        text: "Katalog öğesi silmek için SuperAdmin Master Anahtarı (X-SuperAdmin-Key) zorunludur.",
      });
      return;
    }

    setDeletingCatalogId(id);
    setFeedback(null);

    try {
      await api(`/superadmin/wizard-catalog/${id}`, {
        method: "DELETE",
        headers: {
          "X-SuperAdmin-Key": masterKey.trim(),
        },
      });
      setCatalogItems((prev) => prev.filter((item) => item.id !== id));
      setFeedback({
        type: "success",
        text: "Katalog öğesi silindi.",
      });
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setDeletingCatalogId(null);
    }
  };

  if (!session.isSuperAdmin) {
    return (
      <div className="p-6 max-w-3xl mx-auto">
        <Alert variant="destructive" className="rounded-2xl">
          <AlertCircle className="w-4 h-4" />
          <AlertDescription>
            Bu ekrana yalnızca doğrulanmış SuperAdmin yetkisine sahip hesaplar
            erişebilir.
          </AlertDescription>
        </Alert>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-[1400px] mx-auto space-y-6">
      {/* Üst Başlık ve Zero-Trust Master PIN Koruması */}
      <div className="bg-slate-900 text-white rounded-3xl p-6 space-y-4 shadow-md">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-4">
          <div className="space-y-1">
            <div className="flex items-center gap-2">
              <ShieldCheck className="w-5 h-5 text-violet-400" />
              <span className="text-xs font-bold uppercase tracking-wider text-violet-300">
                4 KATMANLI ZERO-TRUST GÜVENLİK
              </span>
            </div>
            <h1 className="text-2xl font-bold tracking-tight">
              SuperAdmin Sistem &amp; Paket Yönetimi
            </h1>
            <p className="text-xs text-slate-300">
              Aktif oturum: <strong className="text-white">{session.email}</strong>{" "}
              — Yazma işlemleri için Master PIN doğrulaması zorunludur.
            </p>
          </div>

          <div className="w-full lg:w-96 space-y-1.5">
            <Label className="text-xs font-semibold text-slate-200 flex items-center gap-1.5">
              <KeyRound className="w-3.5 h-3.5 text-amber-400" />
              SuperAdmin Master Anahtarı (X-SuperAdmin-Key)
            </Label>
            <div className="relative">
              <Lock className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
              <Input
                type="password"
                value={masterKey}
                onChange={(e) => setMasterKey(e.target.value)}
                placeholder="Master güvenlik anahtarınızı girin..."
                className="pl-9 h-10 rounded-xl bg-slate-800 border-slate-700 text-white placeholder:text-slate-400 text-xs"
              />
            </div>
          </div>
        </div>
      </div>

      {feedback && (
        <Alert
          variant={feedback.type === "error" ? "destructive" : "default"}
          className={
            feedback.type === "success"
              ? "border-emerald-200 bg-emerald-50 text-emerald-900 rounded-2xl"
              : "rounded-2xl"
          }
        >
          <div className="flex items-center gap-2">
            {feedback.type === "success" ? (
              <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
            ) : (
              <AlertCircle className="w-4 h-4 shrink-0" />
            )}
            <AlertDescription className="text-xs font-medium">
              {feedback.text}
            </AlertDescription>
          </div>
        </Alert>
      )}

      <Tabs
        value={activeTab}
        onValueChange={(v) => setActiveTab(v as "plans" | "catalog")}
        className="space-y-6"
      >
        <div className="flex items-center justify-between flex-wrap gap-3">
          <TabsList className="bg-slate-100 p-1 rounded-xl">
            <TabsTrigger value="plans" className="text-xs px-4 py-2 rounded-lg">
              <Database className="w-3.5 h-3.5 mr-1.5" />
              Paket Limitleri Yönetimi
            </TabsTrigger>
            <TabsTrigger
              value="catalog"
              className="text-xs px-4 py-2 rounded-lg"
            >
              <Layers className="w-3.5 h-3.5 mr-1.5" />
              Sihirbaz Dinamik Kataloğu
            </TabsTrigger>
          </TabsList>

          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => void loadSuperAdminData()}
            className="rounded-xl text-xs h-9"
          >
            <RefreshCw
              className={`w-3.5 h-3.5 mr-1.5 ${loading ? "animate-spin" : ""}`}
            />
            Verileri Yenile
          </Button>
        </div>

        {/* SEKME 1: PAKET LİMİTLERİ YÖNETİMİ */}
        <TabsContent value="plans" className="space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
            {plans.map((planItem, idx) => {
              const code = (planItem.planCode ??
                planItem.plan ??
                "STARTER") as PlanTier;
              const storageBytes =
                planItem.maxStorageBytes ??
                planItem.storageLimitBytes ??
                104857600;
              const storageMb = Math.round(storageBytes / (1024 * 1024));
              const isSaving = savingPlanCode === code;

              return (
                <Card
                  key={code}
                  className="rounded-2xl border-slate-200/80 shadow-xs"
                >
                  <CardHeader className="pb-3">
                    <div className="flex items-center justify-between">
                      <Badge className="bg-[#7c3aed] text-white">{code}</Badge>
                      <span className="text-xs text-slate-400 font-mono">
                        /superadmin/plans/{code}
                      </span>
                    </div>
                    <CardTitle className="text-base font-bold text-slate-900 pt-1">
                      {planItem.displayName ?? planItem.name ?? code} Paketi
                    </CardTitle>
                  </CardHeader>

                  <CardContent className="space-y-4">
                    <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Fiyat (₺ / Ay)
                        </Label>
                        <Input
                          type="number"
                          value={planItem.monthlyPriceTry}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "monthlyPriceTry",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Aylık Mesaj Limiti
                        </Label>
                        <Input
                          type="number"
                          value={planItem.monthlyMessageLimit}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "monthlyMessageLimit",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Maks. Ekip Üyesi (Koltuk)
                        </Label>
                        <Input
                          type="number"
                          min={1}
                          value={planItem.maxSeats}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "maxSeats",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Depolama Limiti (MB)
                        </Label>
                        <Input
                          type="number"
                          value={storageMb}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "maxStorageBytes",
                              Number(e.target.value) * 1024 * 1024
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Maksimum Döküman
                        </Label>
                        <Input
                          type="number"
                          value={planItem.maxDocumentCount}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "maxDocumentCount",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label className="text-[11px] text-slate-600">
                          Maksimum URL
                        </Label>
                        <Input
                          type="number"
                          value={planItem.maxUrlSourceCount}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "maxUrlSourceCount",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>

                      <div className="space-y-1 sm:col-span-3">
                        <Label className="text-[11px] text-slate-600">
                          Playground Aylık Test Kredisi
                        </Label>
                        <Input
                          type="number"
                          value={planItem.playgroundMonthlyLimit || 200}
                          onChange={(e) =>
                            updatePlanField(
                              idx,
                              "playgroundMonthlyLimit",
                              Number(e.target.value)
                            )
                          }
                          className="h-9 text-xs rounded-xl"
                        />
                      </div>
                    </div>

                    <Button
                      type="button"
                      disabled={isSaving}
                      onClick={() => void handleSavePlan(planItem)}
                      className="w-full rounded-xl h-9 text-xs font-semibold bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
                    >
                      {isSaving ? (
                        <>
                          <Loader2 className="w-3.5 h-3.5 mr-1.5 animate-spin" />
                          Kaydediliyor...
                        </>
                      ) : (
                        <>
                          <Save className="w-3.5 h-3.5 mr-1.5" />
                          {code} Paket Limitlerini Kaydet
                        </>
                      )}
                    </Button>
                  </CardContent>
                </Card>
              );
            })}
          </div>
        </TabsContent>

        {/* SEKME 2: SİHİRBAZ DİNAMİK KATALOĞU */}
        <TabsContent value="catalog" className="space-y-6">
          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardHeader className="pb-3">
              <CardTitle className="text-base font-bold text-slate-900">
                Yeni Sihirbaz Katalog Seçeneği Ekle
              </CardTitle>
              <CardDescription className="text-xs">
                Ayarlar sihirbazındaki Sektör, Rol, Kanal ve Bilgi Yöntemi
                kartlarını deploy gerektirmeden canlı ekleyin.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <form
                onSubmit={handleAddCatalogItem}
                className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3 items-end"
              >
                <div className="space-y-1">
                  <Label className="text-xs">Kategori</Label>
                  <Select
                    value={newCategory}
                    onValueChange={(v) =>
                      setNewCategory(
                        v as
                          | "USE_CASE"
                          | "ASSISTANT_ROLE"
                          | "PRIMARY_CHANNEL"
                          | "KNOWLEDGE_METHOD"
                      )
                    }
                  >
                    <SelectTrigger className="h-9 text-xs rounded-xl">
                      <SelectValue>{newCategory}</SelectValue>
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="USE_CASE">
                        USE_CASE (Sektör)
                      </SelectItem>
                      <SelectItem value="ASSISTANT_ROLE">
                        ASSISTANT_ROLE (Asistan Rolü)
                      </SelectItem>
                      <SelectItem value="PRIMARY_CHANNEL">
                        PRIMARY_CHANNEL (Ana Kanal)
                      </SelectItem>
                      <SelectItem value="KNOWLEDGE_METHOD">
                        KNOWLEDGE_METHOD (Bilgi Yöntemi)
                      </SelectItem>
                    </SelectContent>
                  </Select>
                </div>

                <div className="space-y-1">
                  <Label className="text-xs">Anahtar (Key)</Label>
                  <Input
                    value={newKey}
                    onChange={(e) => setNewKey(e.target.value)}
                    placeholder="orn: dental_clinic"
                    className="h-9 text-xs rounded-xl"
                  />
                </div>

                <div className="space-y-1">
                  <Label className="text-xs">Başlık</Label>
                  <Input
                    value={newTitle}
                    onChange={(e) => setNewTitle(e.target.value)}
                    placeholder="Örn: Diş Kliniği"
                    className="h-9 text-xs rounded-xl"
                  />
                </div>

                <div className="space-y-1">
                  <Label className="text-xs">İkon Adı (Lucide)</Label>
                  <Input
                    value={newIconName}
                    onChange={(e) => setNewIconName(e.target.value)}
                    placeholder="Sparkles"
                    className="h-9 text-xs rounded-xl"
                  />
                </div>

                <div className="space-y-1 md:col-span-2">
                  <Label className="text-xs">Açıklama</Label>
                  <Input
                    value={newDescription}
                    onChange={(e) => setNewDescription(e.target.value)}
                    placeholder="Randevu, tedavi ve fiyat sorularını yanıtlar."
                    className="h-9 text-xs rounded-xl"
                  />
                </div>

                <div className="space-y-1">
                  <Label className="text-xs">
                    Varsayılan Karşılama Mesajı (Opsiyonel)
                  </Label>
                  <Input
                    value={newWelcome}
                    onChange={(e) => setNewWelcome(e.target.value)}
                    placeholder="Merhaba, kliniğimize hoş geldiniz!"
                    className="h-9 text-xs rounded-xl"
                  />
                </div>

                <Button
                  type="submit"
                  disabled={addingCatalog}
                  className="h-9 rounded-xl bg-[#7c3aed] hover:bg-[#6d28d9] text-white text-xs font-semibold"
                >
                  {addingCatalog ? (
                    <Loader2 className="w-4 h-4 animate-spin" />
                  ) : (
                    <>
                      <Plus className="w-4 h-4 mr-1.5" />
                      Kataloğa Ekle
                    </>
                  )}
                </Button>
              </form>
            </CardContent>
          </Card>

          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardHeader className="pb-3">
              <CardTitle className="text-base font-bold text-slate-900">
                Mevcut Katalog Öğeleri ({catalogItems.length})
              </CardTitle>
            </CardHeader>
            <CardContent className="divide-y divide-slate-100">
              {catalogItems.map((item) => (
                <div
                  key={item.id}
                  className="py-3 flex items-center justify-between gap-4"
                >
                  <div className="space-y-0.5 min-w-0">
                    <div className="flex items-center gap-2">
                      <Badge variant="outline" className="text-[10px]">
                        {item.category}
                      </Badge>
                      <span className="text-xs font-bold text-slate-900">
                        {item.title}
                      </span>
                      <code className="text-[11px] text-slate-400 font-mono">
                        ({item.key})
                      </code>
                    </div>
                    <p className="text-xs text-slate-500 truncate">
                      {item.description}
                    </p>
                  </div>

                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    disabled={deletingCatalogId === item.id}
                    onClick={() => void handleDeleteCatalogItem(item.id)}
                    className="h-8 w-8 rounded-xl text-slate-400 hover:text-rose-600 hover:bg-rose-50 shrink-0"
                  >
                    {deletingCatalogId === item.id ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <Trash2 className="w-4 h-4" />
                    )}
                  </Button>
                </div>
              ))}
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}
