"use client";

import React, { useCallback, useEffect, useState } from "react";
import { api, errorMessage } from "@/lib/api";
import type {
  ChannelIntegrationItem,
  ChannelIntegrationsOverview,
  IntegrationProvider,
  PanelProps,
  Section,
  TestAndConnectResponse,
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
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Separator } from "@/components/ui/separator";
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Camera,
  Check,
  CheckCircle2,
  Copy,
  Globe,
  Loader2,
  MessageCircle,
  Plug,
  RefreshCw,
  ShieldCheck,
  Store,
  Trash2,
} from "lucide-react";

type WizardStep = 1 | 2 | 3 | 4;

interface ProviderMeta {
  provider: IntegrationProvider;
  title: string;
  subtitle: string;
  connectButtonLabel: string;
  icon: React.ComponentType<{ className?: string }>;
  iconBgClass: string;
  iconColorClass: string;
}

const PROVIDERS: ProviderMeta[] = [
  {
    provider: "WHATSAPP",
    title: "WhatsApp Business Cloud API",
    subtitle:
      "Meta WhatsApp Cloud API ile resmi işletme numaranızı bağlayın ve 7/24 yapay zeka ile yanıtlayın.",
    connectButtonLabel: "WhatsApp Business Bağla",
    icon: MessageCircle,
    iconBgClass: "bg-emerald-50",
    iconColorClass: "text-emerald-600",
  },
  {
    provider: "INSTAGRAM",
    title: "Instagram DM Otomasyonu",
    subtitle:
      "Instagram işletme hesabınıza gelen DM ve hikaye yanıtlarını Meta Graph API üzerinden tek gelen kutusunda toplayın.",
    connectButtonLabel: "Instagram Hesabını Bağla",
    icon: Camera,
    iconBgClass: "bg-pink-50",
    iconColorClass: "text-pink-600",
  },
  {
    provider: "TRENDYOL",
    title: "Trendyol Mağaza Soru-Cevap",
    subtitle:
      "Trendyol mağazanıza gelen ürün ve sipariş sorularını resmi API ile saniyeler içinde AI destekli yanıtlayın.",
    connectButtonLabel: "Trendyol Mağazası Bağla",
    icon: Store,
    iconBgClass: "bg-orange-50",
    iconColorClass: "text-orange-600",
  },
];

function formatDateTime(iso?: string | null): string {
  if (!iso) return "Henüz test edilmedi";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "Bilinmiyor";
  return d.toLocaleString("tr-TR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

export function ChannelsPanel({
  workspaceId,
  onNavigate,
}: PanelProps & { onNavigate?: (s: Section) => void }) {
  const [items, setItems] = useState<ChannelIntegrationItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [disconnectingProvider, setDisconnectingProvider] =
    useState<IntegrationProvider | null>(null);
  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  // Modal & 4-Step Wizard State
  const [modalOpen, setModalOpen] = useState(false);
  const [activeProvider, setActiveProvider] =
    useState<IntegrationProvider>("WHATSAPP");
  const [step, setStep] = useState<WizardStep>(1);
  const [copiedField, setCopiedField] = useState<string | null>(null);

  // Form State (Step 2)
  const [phoneNumberId, setPhoneNumberId] = useState("");
  const [wabaId, setWabaId] = useState("");
  const [instagramAccountId, setInstagramAccountId] = useState("");
  const [pageId, setPageId] = useState("");
  const [supplierId, setSupplierId] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [apiSecret, setApiSecret] = useState("");
  const [accessToken, setAccessToken] = useState("");
  const [appSecret, setAppSecret] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  // Live Test State (Step 3 & Step 4)
  const [testing, setTesting] = useState(false);
  const [testError, setTestError] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<TestAndConnectResponse | null>(
    null
  );

  useEffect(() => {
    if (!feedback) return;
    const t = setTimeout(() => setFeedback(null), 5000);
    return () => clearTimeout(t);
  }, [feedback]);

  const loadIntegrations = useCallback(async () => {
    setLoading(true);
    try {
      const res = await api<ChannelIntegrationsOverview>(
        `/workspaces/${workspaceId}/integrations`
      );
      setItems(res.items ?? []);
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setLoading(false);
    }
  }, [workspaceId]);

  useEffect(() => {
    void loadIntegrations();
  }, [loadIntegrations]);

  const getIntegrationItem = (
    provider: IntegrationProvider
  ): ChannelIntegrationItem => {
    const found = items.find((i) => i.provider === provider);
    if (found) return found;
    const origin =
      typeof window !== "undefined"
        ? window.location.origin
        : "http://localhost:58543";
    return {
      provider,
      status: "DISCONNECTED",
      webhookUrl: `${origin}/webhooks/channels/${workspaceId}/${provider.toLowerCase()}`,
      webhookVerifyToken: `daribase_verify_${workspaceId.slice(0, 8)}`,
    };
  };

  const openWizardModal = (provider: IntegrationProvider) => {
    const existing = getIntegrationItem(provider);
    setActiveProvider(provider);
    setStep(1);
    setFormError(null);
    setTestError(null);
    setTestResult(null);

    // Pre-fill existing IDs if reconfiguring
    if (provider === "WHATSAPP") {
      setPhoneNumberId(existing.externalAccountId ?? "");
      setWabaId(existing.secondaryAccountId ?? "");
      setAccessToken("");
      setAppSecret("");
    } else if (provider === "INSTAGRAM") {
      setInstagramAccountId(existing.externalAccountId ?? "");
      setPageId(existing.secondaryAccountId ?? "");
      setAccessToken("");
      setAppSecret("");
    } else {
      setSupplierId(existing.externalAccountId ?? "");
      setApiKey(existing.secondaryAccountId ?? "");
      setApiSecret("");
    }

    setModalOpen(true);
  };

  const handleCopy = async (text: string, fieldKey: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedField(fieldKey);
      setTimeout(() => setCopiedField(null), 2000);
    } catch {
      // ignore clipboard errors
    }
  };

  const validateStep2 = (): boolean => {
    setFormError(null);
    if (activeProvider === "WHATSAPP") {
      if (!phoneNumberId.trim()) {
        setFormError("Phone Number ID alanı zorunludur.");
        return false;
      }
      if (!wabaId.trim()) {
        setFormError("WhatsApp Business Account ID (WABA ID) alanı zorunludur.");
        return false;
      }
      if (!accessToken.trim()) {
        setFormError("Permanent System User Access Token alanı zorunludur.");
        return false;
      }
    } else if (activeProvider === "INSTAGRAM") {
      if (!instagramAccountId.trim()) {
        setFormError("Instagram Business Account ID alanı zorunludur.");
        return false;
      }
      if (!pageId.trim()) {
        setFormError("Connected Facebook Page ID alanı zorunludur.");
        return false;
      }
      if (!accessToken.trim()) {
        setFormError("Page / System Access Token alanı zorunludur.");
        return false;
      }
    } else if (activeProvider === "TRENDYOL") {
      if (!supplierId.trim()) {
        setFormError("Satıcı ID (Supplier ID) alanı zorunludur.");
        return false;
      }
      if (!apiKey.trim()) {
        setFormError("API Key alanı zorunludur.");
        return false;
      }
      if (!apiSecret.trim()) {
        setFormError("API Secret alanı zorunludur.");
        return false;
      }
    }
    return true;
  };

  const runLiveTest = async () => {
    setTesting(true);
    setTestError(null);
    setTestResult(null);

    try {
      const payload =
        activeProvider === "WHATSAPP"
          ? {
              phoneNumberId: phoneNumberId.trim(),
              wabaId: wabaId.trim(),
              accessToken: accessToken.trim(),
              appSecret: appSecret.trim() || null,
            }
          : activeProvider === "INSTAGRAM"
          ? {
              instagramAccountId: instagramAccountId.trim(),
              pageId: pageId.trim(),
              accessToken: accessToken.trim(),
              appSecret: appSecret.trim() || null,
            }
          : {
              supplierId: supplierId.trim(),
              apiKey: apiKey.trim(),
              apiSecret: apiSecret.trim(),
            };

      const res = await api<TestAndConnectResponse>(
        `/workspaces/${workspaceId}/integrations/${activeProvider}/test-and-connect`,
        {
          method: "POST",
          body: JSON.stringify(payload),
        }
      );

      if (!res.success) {
        setTestError(
          res.error ||
            "Bağlantı doğrulaması başarısız oldu. Lütfen API bilgilerinizi kontrol edin."
        );
        return;
      }

      setTestResult(res);
      setStep(4);
      await loadIntegrations();
    } catch (err) {
      setTestError(errorMessage(err));
      await loadIntegrations();
    } finally {
      setTesting(false);
    }
  };

  const handleProceedToStep3 = () => {
    if (!validateStep2()) return;
    setStep(3);
    void runLiveTest();
  };

  const handleDisconnect = async (provider: IntegrationProvider) => {
    setDisconnectingProvider(provider);
    setFeedback(null);
    try {
      await api(`/workspaces/${workspaceId}/integrations/${provider}`, {
        method: "DELETE",
      });
      await loadIntegrations();
      setFeedback({
        type: "success",
        text: `${provider} kanal bağlantısı başarıyla kaldırıldı.`,
      });
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setDisconnectingProvider(null);
    }
  };

  const activeItem = getIntegrationItem(activeProvider);
  const activeMeta =
    PROVIDERS.find((p) => p.provider === activeProvider) ?? PROVIDERS[0];

  const wizardSteps: { num: WizardStep; label: string }[] = [
    { num: 1, label: "Adım 1: Webhook" },
    { num: 2, label: "Adım 2: API Bilgileri" },
    { num: 3, label: "Adım 3: Canlı Test" },
    { num: 4, label: "Adım 4: Sonuç" },
  ];

  return (
    <div className="p-6 max-w-[1400px] mx-auto space-y-6">
      {/* Üst Başlık */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="space-y-1.5">
          <span className="bg-violet-50 text-[#7c3aed] border border-violet-200 text-[11px] font-semibold px-3 py-1 rounded-full inline-flex items-center gap-1.5">
            <Plug className="w-3.5 h-3.5" />
            GERÇEK ZAMANLI KANAL ENTEGRASYONLARI
          </span>
          <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
            Kanallar &amp; Resmi API Bağlantıları
          </h1>
          <p className="text-sm text-slate-500">
            WhatsApp Business Cloud API, Instagram DM ve Trendyol mağazanızı
            canlı doğrulama testiyle yapay zeka asistanınıza bağlayın.
          </p>
        </div>

        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => void loadIntegrations()}
          disabled={loading}
          className="rounded-xl h-9 text-xs font-semibold border-slate-200 self-start sm:self-auto"
        >
          {loading ? (
            <Loader2 className="w-3.5 h-3.5 mr-1.5 animate-spin" />
          ) : (
            <RefreshCw className="w-3.5 h-3.5 mr-1.5" />
          )}
          Durumları Yenile
        </Button>
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
              <AlertTriangle className="w-4 h-4 shrink-0" />
            )}
            <AlertDescription className="text-xs font-medium">
              {feedback.text}
            </AlertDescription>
          </div>
        </Alert>
      )}

      {/* 4 Kanal Kartı (2x2 Grid) */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        {/* Kart 1: Web Sitesi Canlı Destek Widget */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs flex flex-col justify-between">
          <CardHeader className="space-y-3">
            <div className="flex items-center justify-between gap-2">
              <div className="w-11 h-11 rounded-2xl bg-violet-50 text-[#7c3aed] flex items-center justify-center shrink-0">
                <Globe className="w-5 h-5" />
              </div>
              <Badge
                variant="secondary"
                className="bg-emerald-50 text-emerald-700 border border-emerald-200 text-xs font-semibold px-2.5 py-0.5"
              >
                <CheckCircle2 className="w-3.5 h-3.5 mr-1" />
                Aktif &amp; Yayında
              </Badge>
            </div>
            <div>
              <CardTitle className="text-base font-bold text-slate-900">
                Web Sitesi Canlı Destek Widget
              </CardTitle>
              <CardDescription className="text-xs mt-1 leading-relaxed">
                Web sitenize tek satır kodla eklenen veya doğrudan paylaşım
                linkiyle çalışan 7/24 yapay zeka destekli sohbet balonu.
              </CardDescription>
            </div>
          </CardHeader>
          <CardContent className="pt-2 space-y-3">
            <div className="rounded-xl bg-slate-50 border border-slate-200/70 p-3 text-xs text-slate-600 flex items-center justify-between">
              <span className="font-medium text-slate-700">
                Embed Script &amp; Paylaşım Linki
              </span>
              <span className="text-emerald-700 font-semibold">Hazır</span>
            </div>
            <Button
              type="button"
              onClick={() => onNavigate?.("widget")}
              className="w-full bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 text-xs font-semibold"
            >
              Widget &amp; Embed Kodu
              <ArrowRight className="w-4 h-4 ml-1.5" />
            </Button>
          </CardContent>
        </Card>

        {/* Kart 2, 3, 4: WhatsApp, Instagram, Trendyol */}
        {PROVIDERS.map((meta) => {
          const item = getIntegrationItem(meta.provider);
          const Icon = meta.icon;
          const isConnected = item.status === "CONNECTED";
          const isError = item.status === "ERROR";
          const isDisconnecting = disconnectingProvider === meta.provider;

          return (
            <Card
              key={meta.provider}
              className={`rounded-2xl shadow-xs flex flex-col justify-between transition-all ${
                isConnected
                  ? "border-emerald-200/90 bg-white"
                  : isError
                  ? "border-rose-200/90 bg-white"
                  : "border-slate-200/80 bg-white"
              }`}
            >
              <CardHeader className="space-y-3">
                <div className="flex items-center justify-between gap-2">
                  <div
                    className={`w-11 h-11 rounded-2xl ${meta.iconBgClass} ${meta.iconColorClass} flex items-center justify-center shrink-0`}
                  >
                    <Icon className="w-5 h-5" />
                  </div>

                  {isConnected ? (
                    <Badge
                      variant="secondary"
                      className="bg-emerald-50 text-emerald-700 border border-emerald-200 text-xs font-semibold px-2.5 py-0.5"
                    >
                      <CheckCircle2 className="w-3.5 h-3.5 mr-1" />
                      Bağlı
                    </Badge>
                  ) : isError ? (
                    <Badge
                      variant="secondary"
                      className="bg-rose-50 text-rose-700 border border-rose-200 text-xs font-semibold px-2.5 py-0.5"
                    >
                      <AlertTriangle className="w-3.5 h-3.5 mr-1" />
                      Doğrulama Hatası
                    </Badge>
                  ) : (
                    <Badge
                      variant="secondary"
                      className="bg-slate-100 text-slate-600 border border-slate-200 text-xs font-medium px-2.5 py-0.5"
                    >
                      Bağlı Değil
                    </Badge>
                  )}
                </div>

                <div>
                  <CardTitle className="text-base font-bold text-slate-900">
                    {meta.title}
                  </CardTitle>
                  <CardDescription className="text-xs mt-1 leading-relaxed">
                    {meta.subtitle}
                  </CardDescription>
                </div>
              </CardHeader>

              <CardContent className="pt-2 space-y-3">
                {isConnected ? (
                  <div className="rounded-xl bg-emerald-50/60 border border-emerald-200/80 p-3.5 space-y-2 text-xs">
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-slate-500 font-medium">
                        Doğrulanmış Hesap:
                      </span>
                      <span className="font-bold text-slate-900 truncate">
                        {item.displayName || item.externalAccountId}
                      </span>
                    </div>

                    {meta.provider === "WHATSAPP" && (
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-slate-500 font-medium">
                          Meta Kalite Puanı:
                        </span>
                        <Badge
                          variant="secondary"
                          className="bg-emerald-100 text-emerald-800 border-emerald-200 text-[11px] font-bold"
                        >
                          {item.verifiedMetadata?.qualityRating || "GREEN"}
                        </Badge>
                      </div>
                    )}

                    {meta.provider === "INSTAGRAM" &&
                      item.verifiedMetadata?.username && (
                        <div className="flex items-center justify-between gap-2">
                          <span className="text-slate-500 font-medium">
                            Kullanıcı Adı:
                          </span>
                          <span className="font-semibold text-slate-800">
                            @{item.verifiedMetadata.username}
                          </span>
                        </div>
                      )}

                    {meta.provider === "TRENDYOL" && item.externalAccountId && (
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-slate-500 font-medium">
                          Satıcı ID (Supplier ID):
                        </span>
                        <span className="font-semibold text-slate-800">
                          {item.externalAccountId}
                        </span>
                      </div>
                    )}

                    <div className="flex items-center justify-between gap-2 text-[11px] text-slate-500 pt-1 border-t border-emerald-200/60">
                      <span>Son Doğrulama:</span>
                      <span className="font-medium text-slate-700">
                        {formatDateTime(item.lastTestedAt)}
                      </span>
                    </div>
                  </div>
                ) : isError && item.lastErrorMessage ? (
                  <div className="rounded-xl bg-rose-50 border border-rose-200 p-3 text-xs text-rose-800 space-y-1">
                    <div className="font-semibold flex items-center gap-1.5">
                      <AlertTriangle className="w-3.5 h-3.5 shrink-0" />
                      Son Test Hatası:
                    </div>
                    <p className="text-[11px] leading-relaxed line-clamp-2">
                      {item.lastErrorMessage}
                    </p>
                  </div>
                ) : (
                  <div className="rounded-xl bg-slate-50 border border-slate-200/70 p-3 text-xs text-slate-500 flex items-center justify-between">
                    <span>Gerçek API &amp; Webhook Doğrulaması</span>
                    <span className="font-semibold text-slate-700">
                      4 Adımda Kurulum
                    </span>
                  </div>
                )}

                {isConnected ? (
                  <div className="flex items-center gap-2.5">
                    <Button
                      type="button"
                      variant="outline"
                      onClick={() => openWizardModal(meta.provider)}
                      className="flex-1 rounded-xl h-10 text-xs font-semibold border-slate-200"
                    >
                      <RefreshCw className="w-3.5 h-3.5 mr-1.5" />
                      Yeniden Yapılandır / Test Et
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      disabled={isDisconnecting}
                      onClick={() => void handleDisconnect(meta.provider)}
                      className="rounded-xl h-10 px-3.5 text-xs font-semibold border-rose-200 text-rose-600 hover:bg-rose-50 hover:text-rose-700"
                    >
                      {isDisconnecting ? (
                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      ) : (
                        <>
                          <Trash2 className="w-3.5 h-3.5 mr-1.5" />
                          Bağlantıyı Kes
                        </>
                      )}
                    </Button>
                  </div>
                ) : (
                  <Button
                    type="button"
                    onClick={() => openWizardModal(meta.provider)}
                    className="w-full bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 text-xs font-semibold"
                  >
                    <Plug className="w-4 h-4 mr-1.5" />
                    {meta.connectButtonLabel}
                  </Button>
                )}
              </CardContent>
            </Card>
          );
        })}
      </div>

      {/* 4 Adımlı Canlı Kurulum, Test ve Onay Modalı */}
      <Dialog open={modalOpen} onOpenChange={setModalOpen}>
        <DialogContent className="rounded-2xl sm:max-w-xl p-6 space-y-5">
          <DialogHeader className="space-y-2">
            <div className="flex items-center gap-2.5">
              <div
                className={`w-9 h-9 rounded-xl ${activeMeta.iconBgClass} ${activeMeta.iconColorClass} flex items-center justify-center shrink-0`}
              >
                <activeMeta.icon className="w-5 h-5" />
              </div>
              <div>
                <DialogTitle className="text-base font-bold text-slate-900">
                  {activeMeta.title} — Canlı Kurulum Sihirbazı
                </DialogTitle>
                <DialogDescription className="text-xs text-slate-500">
                  Resmi API kimlik bilgilerinizi girin ve canlı bağlantı testi
                  ile doğrulayın.
                </DialogDescription>
              </div>
            </div>

            {/* 4 Adımlı İlerleme Çubuğu */}
            <div className="grid grid-cols-4 gap-2 pt-2">
              {wizardSteps.map((s) => {
                const isCurrent = step === s.num;
                const isPassed = step > s.num;
                return (
                  <div
                    key={s.num}
                    className={`rounded-xl px-2.5 py-2 border text-center transition-all ${
                      isCurrent
                        ? "bg-violet-50 border-[#7c3aed] text-[#7c3aed] font-bold"
                        : isPassed
                        ? "bg-emerald-50 border-emerald-200 text-emerald-700 font-semibold"
                        : "bg-slate-50 border-slate-200 text-slate-400 font-medium"
                    }`}
                  >
                    <div className="text-[11px] truncate">{s.label}</div>
                  </div>
                );
              })}
            </div>
          </DialogHeader>

          <Separator />

          {/* ADIM 1/4: Webhook Hazırlığı */}
          {step === 1 && (
            <div className="space-y-4">
              <div className="rounded-2xl bg-slate-50 border border-slate-200/80 p-4 space-y-3">
                <div className="flex items-center gap-2 text-xs font-bold text-slate-800">
                  <ShieldCheck className="w-4 h-4 text-[#7c3aed]" />
                  {activeProvider === "TRENDYOL"
                    ? "Adım 1/4 — Trendyol Webhook & Bildirim Adresi"
                    : "Adım 1/4 — Meta Developers Webhook Yapılandırması"}
                </div>
                <p className="text-xs text-slate-600 leading-relaxed">
                  {activeProvider === "TRENDYOL"
                    ? "Trendyol mağaza entegrasyonunuzda anlık soru bildirimlerini almak için aşağıdaki Webhook URL bilgisini kullanabilir veya doğrudan 2. adımda API anahtarlarınızı girebilirsiniz."
                    : "Meta Developers (developers.facebook.com) panelinizde uygulamanızın Webhook ayarlarına aşağıdaki Callback URL ve Verify Token değerlerini yapıştırın:"}
                </p>

                {/* Callback URL */}
                <div className="space-y-1.5">
                  <Label className="text-xs font-semibold text-slate-700">
                    Webhook Callback URL
                  </Label>
                  <div className="flex items-center gap-2">
                    <Input
                      readOnly
                      value={activeItem.webhookUrl}
                      className="h-9 text-xs font-mono bg-white rounded-xl"
                    />
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        void handleCopy(activeItem.webhookUrl, "webhookUrl")
                      }
                      className="h-9 px-3 rounded-xl shrink-0 text-xs font-semibold"
                    >
                      {copiedField === "webhookUrl" ? (
                        <>
                          <Check className="w-3.5 h-3.5 mr-1 text-emerald-600" />
                          Kopyalandı
                        </>
                      ) : (
                        <>
                          <Copy className="w-3.5 h-3.5 mr-1" />
                          Kopyala
                        </>
                      )}
                    </Button>
                  </div>
                </div>

                {/* Verify Token */}
                <div className="space-y-1.5">
                  <Label className="text-xs font-semibold text-slate-700">
                    Webhook Verify Token
                  </Label>
                  <div className="flex items-center gap-2">
                    <Input
                      readOnly
                      value={activeItem.webhookVerifyToken}
                      className="h-9 text-xs font-mono bg-white rounded-xl"
                    />
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() =>
                        void handleCopy(
                          activeItem.webhookVerifyToken,
                          "verifyToken"
                        )
                      }
                      className="h-9 px-3 rounded-xl shrink-0 text-xs font-semibold"
                    >
                      {copiedField === "verifyToken" ? (
                        <>
                          <Check className="w-3.5 h-3.5 mr-1 text-emerald-600" />
                          Kopyalandı
                        </>
                      ) : (
                        <>
                          <Copy className="w-3.5 h-3.5 mr-1" />
                          Kopyala
                        </>
                      )}
                    </Button>
                  </div>
                </div>
              </div>

              <div className="rounded-xl border border-violet-200 bg-violet-50/50 p-3.5 text-xs text-slate-700 space-y-1">
                <div className="font-semibold text-slate-900">
                  Hızlı Kurulum Yönlendirmesi:
                </div>
                {activeProvider === "WHATSAPP" && (
                  <ol className="list-decimal list-inside space-y-1 text-[11px] text-slate-600">
                    <li>
                      Meta Developers panelinde <strong>WhatsApp &gt; Configuration</strong> sekmesine gidin.
                    </li>
                    <li>
                      Yukarıdaki <strong>Callback URL</strong> ve <strong>Verify Token</strong> değerlerini yapıştırıp <strong>Verify and save</strong> butonuna basın.
                    </li>
                    <li>
                      Webhook alanlarından <strong>messages</strong> aboneliğini aktif edin.
                    </li>
                  </ol>
                )}
                {activeProvider === "INSTAGRAM" && (
                  <ol className="list-decimal list-inside space-y-1 text-[11px] text-slate-600">
                    <li>
                      Meta Developers panelinde <strong>Webhooks &gt; Instagram</strong> bölümünü seçin.
                    </li>
                    <li>
                      Yukarıdaki <strong>Callback URL</strong> ve <strong>Verify Token</strong> bilgilerini kaydedin.
                    </li>
                    <li>
                      <strong>messages</strong> ve <strong>messaging_postbacks</strong> bildirimlerini aktif edin.
                    </li>
                  </ol>
                )}
                {activeProvider === "TRENDYOL" && (
                  <ol className="list-decimal list-inside space-y-1 text-[11px] text-slate-600">
                    <li>
                      Trendyol Satıcı Paneli &gt; <strong>Hesap Bilgilerim &gt; Entegrasyon Bilgileri</strong> ekranını açın.
                    </li>
                    <li>
                      <strong>Satıcı ID (Supplier ID)</strong>, <strong>API Key</strong> ve <strong>API Secret</strong> değerlerinizi kopyalayın.
                    </li>
                  </ol>
                )}
              </div>

              <div className="flex items-center justify-end pt-2">
                <Button
                  type="button"
                  onClick={() => setStep(2)}
                  className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 px-5 text-xs font-semibold"
                >
                  Devam Et: API Bilgilerini Gir
                  <ArrowRight className="w-4 h-4 ml-1.5" />
                </Button>
              </div>
            </div>
          )}

          {/* ADIM 2/4: Gerçek API Kimlik Bilgileri */}
          {step === 2 && (
            <div className="space-y-4">
              {formError && (
                <Alert variant="destructive" className="rounded-xl">
                  <div className="flex items-center gap-2">
                    <AlertTriangle className="w-4 h-4 shrink-0" />
                    <AlertDescription className="text-xs font-medium">
                      {formError}
                    </AlertDescription>
                  </div>
                </Alert>
              )}

              {activeProvider === "WHATSAPP" && (
                <div className="space-y-3.5">
                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Phone Number ID <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={phoneNumberId}
                      onChange={(e) => setPhoneNumberId(e.target.value)}
                      placeholder="Örn: 109283746510293"
                      className="h-9 text-xs rounded-xl"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      WhatsApp Business Account ID (WABA ID){" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={wabaId}
                      onChange={(e) => setWabaId(e.target.value)}
                      placeholder="Örn: 102938475610293"
                      className="h-9 text-xs rounded-xl"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Permanent System User Access Token{" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      type="password"
                      value={accessToken}
                      onChange={(e) => setAccessToken(e.target.value)}
                      placeholder="EAAG..."
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-700">
                      Meta App Secret (Opsiyonel - Webhook İmza Güvenliği İçin)
                    </Label>
                    <Input
                      type="password"
                      value={appSecret}
                      onChange={(e) => setAppSecret(e.target.value)}
                      placeholder="Uygulama gizli anahtarı (X-Hub-Signature-256 doğrulaması)"
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>
                </div>
              )}

              {activeProvider === "INSTAGRAM" && (
                <div className="space-y-3.5">
                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Instagram Business Account ID{" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={instagramAccountId}
                      onChange={(e) => setInstagramAccountId(e.target.value)}
                      placeholder="Örn: 17841400000000000"
                      className="h-9 text-xs rounded-xl"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Connected Facebook Page ID{" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={pageId}
                      onChange={(e) => setPageId(e.target.value)}
                      placeholder="Örn: 104928374659102"
                      className="h-9 text-xs rounded-xl"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Page / System Access Token{" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      type="password"
                      value={accessToken}
                      onChange={(e) => setAccessToken(e.target.value)}
                      placeholder="EAAG..."
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-700">
                      Meta App Secret (Opsiyonel)
                    </Label>
                    <Input
                      type="password"
                      value={appSecret}
                      onChange={(e) => setAppSecret(e.target.value)}
                      placeholder="Uygulama gizli anahtarı (Opsiyonel)"
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>
                </div>
              )}

              {activeProvider === "TRENDYOL" && (
                <div className="space-y-3.5">
                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      Satıcı ID (Supplier ID){" "}
                      <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={supplierId}
                      onChange={(e) => setSupplierId(e.target.value)}
                      placeholder="Örn: 123456"
                      className="h-9 text-xs rounded-xl"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      API Key <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      value={apiKey}
                      onChange={(e) => setApiKey(e.target.value)}
                      placeholder="Trendyol API Key"
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>

                  <div className="space-y-1.5">
                    <Label className="text-xs font-semibold text-slate-800">
                      API Secret <span className="text-rose-500">*</span>
                    </Label>
                    <Input
                      type="password"
                      value={apiSecret}
                      onChange={(e) => setApiSecret(e.target.value)}
                      placeholder="Trendyol API Secret"
                      className="h-9 text-xs rounded-xl font-mono"
                    />
                  </div>
                </div>
              )}

              <div className="flex items-center justify-between pt-2">
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => setStep(1)}
                  className="rounded-xl h-10 px-4 text-xs font-semibold border-slate-200"
                >
                  <ArrowLeft className="w-4 h-4 mr-1.5" />
                  Geri
                </Button>

                <Button
                  type="button"
                  onClick={handleProceedToStep3}
                  className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 px-5 text-xs font-semibold"
                >
                  Adım 3&apos;e Geç: Bağlantıyı Test Et
                  <ArrowRight className="w-4 h-4 ml-1.5" />
                </Button>
              </div>
            </div>
          )}

          {/* ADIM 3/4: Canlı Bağlantı Testi ve Doğrulama */}
          {step === 3 && (
            <div className="space-y-4">
              {testing ? (
                <div className="rounded-2xl border border-slate-200 bg-slate-50/80 p-8 flex flex-col items-center justify-center text-center space-y-3">
                  <div className="w-12 h-12 rounded-2xl bg-violet-100 text-[#7c3aed] flex items-center justify-center">
                    <Loader2 className="w-6 h-6 animate-spin" />
                  </div>
                  <div className="space-y-1">
                    <h3 className="text-sm font-bold text-slate-900">
                      Canlı API Doğrulaması Yapılıyor...
                    </h3>
                    <p className="text-xs text-slate-500 max-w-md">
                      {activeProvider === "TRENDYOL"
                        ? "Trendyol API sunucularına bağlanılıyor ve Satıcı ID / API anahtarları doğrulanıyor..."
                        : "Meta Graph API sunucularına bağlanılıyor ve Access Token doğrulanıyor..."}
                    </p>
                  </div>
                </div>
              ) : testError ? (
                <div className="bg-rose-50 border border-rose-200 text-rose-800 p-4 rounded-2xl space-y-3">
                  <div className="flex items-center gap-2 font-bold text-sm text-rose-900">
                    <AlertTriangle className="w-5 h-5 text-rose-600 shrink-0" />
                    Bağlantı Doğrulanamadı!
                  </div>

                  <div className="rounded-xl bg-white/90 border border-rose-200/80 p-3 text-xs font-mono text-rose-900 break-words">
                    {testError}
                  </div>

                  <div className="space-y-1.5 pt-1">
                    <div className="text-xs font-bold text-rose-900">
                      Olası Sebepler ve Kontrol Listesi:
                    </div>
                    <ul className="list-disc list-inside text-xs text-rose-800 space-y-1">
                      {activeProvider === "WHATSAPP" && (
                        <>
                          <li>
                            Access Token süresi dolmuş veya hatalı kopyalanmış
                            olabilir.
                          </li>
                          <li>
                            Phone Number ID ile WABA ID aynı Meta uygulamasına
                            ait olmayabilir.
                          </li>
                          <li>
                            System User kullanıcısına{" "}
                            <strong>whatsapp_business_messaging</strong> ve{" "}
                            <strong>whatsapp_business_management</strong>{" "}
                            izinleri verilmemiş olabilir.
                          </li>
                        </>
                      )}
                      {activeProvider === "INSTAGRAM" && (
                        <>
                          <li>
                            Page / System Access Token süresi dolmuş veya geçersiz
                            olabilir.
                          </li>
                          <li>
                            Instagram Business Account ID değeri profesyonel bir
                            işletme hesabına ait olmayabilir.
                          </li>
                          <li>
                            Uygulamanıza{" "}
                            <strong>instagram_manage_messages</strong> yetkisi
                            tanımlanmamış olabilir.
                          </li>
                        </>
                      )}
                      {activeProvider === "TRENDYOL" && (
                        <>
                          <li>
                            Satıcı ID (Supplier ID), API Key veya API Secret
                            hatalı girilmiş olabilir.
                          </li>
                          <li>
                            Trendyol Satıcı Panelinde API erişim yetkisi aktif
                            olmayabilir.
                          </li>
                        </>
                      )}
                    </ul>
                  </div>

                  <div className="flex items-center justify-between gap-3 pt-2">
                    <Button
                      type="button"
                      variant="outline"
                      onClick={() => setStep(2)}
                      className="rounded-xl h-10 px-4 text-xs font-semibold border-rose-200 bg-white text-rose-900 hover:bg-rose-100"
                    >
                      <ArrowLeft className="w-4 h-4 mr-1.5" />
                      Bilgileri Düzenle (Adım 2&apos;ye Dön)
                    </Button>

                    <Button
                      type="button"
                      onClick={() => void runLiveTest()}
                      className="bg-rose-600 hover:bg-rose-700 text-white rounded-xl h-10 px-4 text-xs font-semibold"
                    >
                      <RefreshCw className="w-4 h-4 mr-1.5" />
                      Tekrar Test Et
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="rounded-2xl border border-slate-200 bg-slate-50 p-6 text-center space-y-3">
                  <Plug className="w-8 h-8 text-[#7c3aed] mx-auto" />
                  <div className="space-y-1">
                    <h3 className="text-sm font-bold text-slate-900">
                      Canlı Bağlantı Testine Hazır
                    </h3>
                    <p className="text-xs text-slate-500">
                      Girdiğiniz kimlik bilgilerini doğrulamak için aşağıdaki
                      butona tıklayın.
                    </p>
                  </div>
                  <Button
                    type="button"
                    onClick={() => void runLiveTest()}
                    className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 px-5 text-xs font-semibold"
                  >
                    <Plug className="w-4 h-4 mr-1.5" />
                    Şimdi Canlı Test Başlat
                  </Button>
                </div>
              )}
            </div>
          )}

          {/* ADIM 4/4: Bağlantı Başarılı ve Doğrulanmış Hesap Özeti */}
          {step === 4 && testResult && (
            <div className="space-y-4">
              <div className="text-center space-y-2 py-2">
                <div className="w-12 h-12 rounded-2xl bg-emerald-100 text-emerald-600 flex items-center justify-center mx-auto">
                  <CheckCircle2 className="w-7 h-7" />
                </div>
                <h3 className="text-base font-bold text-slate-900">
                  Bağlantı Başarıyla Kuruldu!
                </h3>
                <p className="text-xs text-slate-500">
                  Kanalınız canlı olarak doğrulandı ve yapay zeka asistanınıza
                  bağlandı.
                </p>
              </div>

              <div className="bg-emerald-50/70 border border-emerald-200 rounded-2xl p-4 space-y-2.5 text-xs">
                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-600 font-medium">
                    Doğrulanan İşletme / Hesap:
                  </span>
                  <span className="font-bold text-slate-900">
                    {testResult.displayName || activeItem.displayName}
                  </span>
                </div>

                {activeProvider === "WHATSAPP" && (
                  <>
                    {testResult.verifiedMetadata?.displayPhoneNumber && (
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-slate-600 font-medium">
                          Telefon Numarası:
                        </span>
                        <span className="font-semibold text-slate-900">
                          {testResult.verifiedMetadata.displayPhoneNumber}
                        </span>
                      </div>
                    )}
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-slate-600 font-medium">
                        Meta Kalite Puanı:
                      </span>
                      <Badge
                        variant="secondary"
                        className="bg-emerald-100 text-emerald-800 border-emerald-300 text-[11px] font-bold"
                      >
                        {testResult.verifiedMetadata?.qualityRating || "GREEN"}
                      </Badge>
                    </div>
                  </>
                )}

                {activeProvider === "INSTAGRAM" &&
                  testResult.verifiedMetadata?.username && (
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-slate-600 font-medium">
                        Kullanıcı Adı:
                      </span>
                      <span className="font-semibold text-slate-900">
                        @{testResult.verifiedMetadata.username}
                      </span>
                    </div>
                  )}

                <Separator className="bg-emerald-200/70" />

                <div className="flex items-start justify-between gap-2 pt-0.5">
                  <span className="text-slate-600 font-medium shrink-0">
                    Durum:
                  </span>
                  <span className="font-semibold text-emerald-800 text-right">
                    Aktif — Gelen mesajlar otomatik olarak AI ve Gelen Kutusuna
                    yönlendiriliyor.
                  </span>
                </div>
              </div>

              <div className="flex items-center justify-end pt-2">
                <Button
                  type="button"
                  onClick={() => setModalOpen(false)}
                  className="w-full bg-slate-900 hover:bg-slate-800 text-white rounded-xl h-10 text-xs font-semibold"
                >
                  Tamamla ve Kanallara Dön
                </Button>
              </div>
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
