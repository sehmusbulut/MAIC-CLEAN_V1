"use client";

import React, { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "@/lib/api";
import type { PanelProps, WidgetManagementConfig } from "@/lib/types";
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
import { Switch } from "@/components/ui/switch";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Code2,
  Copy,
  Check,
  ExternalLink,
  RefreshCw,
  Globe,
  ShieldCheck,
  Sparkles,
  Share2,
  Lock,
  Loader2,
  AlertCircle,
  CheckCircle2,
} from "lucide-react";

export function WidgetPanel({ workspaceId, onSessionRefresh }: PanelProps) {
  const [config, setConfig] = useState<WidgetManagementConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [togglingShare, setTogglingShare] = useState(false);
  const [regenerating, setRegenerating] = useState(false);
  const [confirmRegenerateOpen, setConfirmRegenerateOpen] = useState(false);

  const [directShareActive, setDirectShareActive] = useState(true);
  const [allowedDomains, setAllowedDomains] = useState("");

  const [copiedEmbed, setCopiedEmbed] = useState(false);
  const [copiedShareLink, setCopiedShareLink] = useState(false);
  const [origin, setOrigin] = useState("http://localhost:3002");

  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  useEffect(() => {
    if (typeof window !== "undefined") {
      setOrigin(window.location.origin);
    }
  }, []);

  useEffect(() => {
    if (!feedback) return;
    const timer = setTimeout(() => setFeedback(null), 4000);
    return () => clearTimeout(timer);
  }, [feedback]);

  const loadWidgetConfig = useCallback(async () => {
    setLoading(true);
    try {
      let data: WidgetManagementConfig;
      try {
        data = await api<WidgetManagementConfig>(
          `/workspaces/${workspaceId}/widget-config`
        );
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) {
          data = await api<WidgetManagementConfig>(
            `/workspaces/${workspaceId}/settings`
          );
        } else {
          throw err;
        }
      }
      setConfig(data);
      setDirectShareActive(data.directShareActive ?? true);
      setAllowedDomains(data.allowedDomains ?? "");
    } catch (err) {
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "Widget yapılandırması yüklenemedi.",
      });
    } finally {
      setLoading(false);
    }
  }, [workspaceId]);

  useEffect(() => {
    void loadWidgetConfig();
  }, [loadWidgetConfig]);

  const persistWidgetConfig = async (
    nextDirectShareActive: boolean,
    nextAllowedDomains: string,
    isToggleOnly = false
  ) => {
    if (isToggleOnly) {
      setTogglingShare(true);
    } else {
      setSaving(true);
    }
    setFeedback(null);

    try {
      let updated: WidgetManagementConfig;
      try {
        updated = await api<WidgetManagementConfig>(
          `/workspaces/${workspaceId}/widget-config`,
          {
            method: "PUT",
            body: JSON.stringify({
              directShareActive: nextDirectShareActive,
              allowedDomains: nextAllowedDomains,
            }),
          }
        );
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) {
          updated = await api<WidgetManagementConfig>(
            `/workspaces/${workspaceId}/settings`,
            {
              method: "PUT",
              body: JSON.stringify({
                directShareActive: nextDirectShareActive,
                allowedDomains: nextAllowedDomains,
              }),
            }
          );
        } else {
          throw err;
        }
      }

      setConfig(updated);
      setDirectShareActive(updated.directShareActive);
      setAllowedDomains(updated.allowedDomains ?? "");
      setFeedback({
        type: "success",
        text: isToggleOnly
          ? updated.directShareActive
            ? "Direkt paylaşım bağlantısı aktif edildi."
            : "Direkt paylaşım bağlantısı devre dışı bırakıldı."
          : "Güvenlik ve widget dağıtım ayarları başarıyla kaydedildi.",
      });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "Ayarlar kaydedilirken bir hata oluştu.",
      });
    } finally {
      setTogglingShare(false);
      setSaving(false);
    }
  };

  const handleToggleDirectShare = async (checked: boolean) => {
    setDirectShareActive(checked);
    await persistWidgetConfig(checked, allowedDomains, true);
  };

  const handleSaveSecurity = async () => {
    await persistWidgetConfig(directShareActive, allowedDomains, false);
  };

  const handleRegenerateKey = async () => {
    setRegenerating(true);
    setFeedback(null);
    try {
      const res = await api<{ directShareKey: string; directShareUrl: string }>(
        `/workspaces/${workspaceId}/widget-config/regenerate-key`,
        {
          method: "POST",
        }
      );
      setConfig((prev) =>
        prev
          ? {
              ...prev,
              directShareKey: res.directShareKey,
              directShareUrl: res.directShareUrl,
            }
          : prev
      );
      setConfirmRegenerateOpen(false);
      setFeedback({
        type: "success",
        text: "Paylaşım anahtarı yenilendi. Eski bağlantı artık geçersizdir.",
      });
    } catch (err) {
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "Paylaşım anahtarı yenilenemedi.",
      });
    } finally {
      setRegenerating(false);
    }
  };

  const embedCode = `<script src="${origin}/embed.js" data-workspace-id="${workspaceId}"></script>`;
  const shareKey = config?.directShareKey ?? "";
  const fullShareUrl = `${origin}/c/${workspaceId}?key=${shareKey}`;

  const copyEmbedCode = async () => {
    try {
      await navigator.clipboard.writeText(embedCode);
      setCopiedEmbed(true);
      setTimeout(() => setCopiedEmbed(false), 2000);
    } catch {
      setFeedback({ type: "error", text: "Kod panoya kopyalanamadı." });
    }
  };

  const copyShareUrl = async () => {
    try {
      await navigator.clipboard.writeText(fullShareUrl);
      setCopiedShareLink(true);
      setTimeout(() => setCopiedShareLink(false), 2000);
    } catch {
      setFeedback({ type: "error", text: "Link panoya kopyalanamadı." });
    }
  };

  const openLiveBotInNewTab = () => {
    if (typeof window !== "undefined") {
      window.open(fullShareUrl, "_blank", "noopener,noreferrer");
    }
  };

  if (loading && !config) {
    return (
      <div className="p-6 max-w-6xl mx-auto flex items-center justify-center min-h-[420px]">
        <div className="flex flex-col items-center gap-3 text-slate-500">
          <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed]" />
          <p className="text-sm font-medium">
            Widget &amp; kurulum bilgileri yükleniyor...
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-6xl mx-auto space-y-6">
      {/* 1. Üst Başlık Alanı */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
              Widget &amp; Kurulum
            </h1>
            <Badge className="bg-violet-50 text-[#7c3aed] border-violet-200 hover:bg-violet-50">
              Canlı Dağıtım
            </Badge>
          </div>
          <p className="text-sm text-slate-500 mt-1">
            Web sitenize canlı destek balonunu ekleyin veya doğrudan paylaşım
            linkiyle müşterilerinizle konuşmaya başlayın.
          </p>
        </div>

        <Button
          variant="outline"
          onClick={openLiveBotInNewTab}
          disabled={!directShareActive || !shareKey}
          className="h-10 rounded-xl border-slate-200 bg-white hover:bg-slate-50 text-slate-800 font-semibold shadow-sm shrink-0"
        >
          <ExternalLink className="w-4 h-4 mr-2 text-[#7c3aed]" />
          Canlı Botu Yeni Sekmede Test Et
        </Button>
      </div>

      {feedback && (
        <Alert
          variant={feedback.type === "error" ? "destructive" : "default"}
          className={
            feedback.type === "success"
              ? "border-emerald-200 bg-emerald-50 text-emerald-900 rounded-xl"
              : "rounded-xl"
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

      {/* 2. İki Sütunlu Ana Grid */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* SOL SÜTUN */}
        <div className="space-y-6">
          {/* Kart 1 — Web Sitenize Ekleyin (EMBED.JS) */}
          <Card className="rounded-2xl border-slate-200/80 shadow-sm">
            <CardHeader className="pb-4">
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2.5">
                  <div className="w-9 h-9 rounded-xl bg-violet-50 text-[#7c3aed] flex items-center justify-center">
                    <Code2 className="w-5 h-5" />
                  </div>
                  <div>
                    <CardTitle className="text-base">
                      Web Sitenize Ekleyin
                    </CardTitle>
                    <CardDescription className="text-xs mt-0.5">
                      Aşağıdaki tek satırlık kodu sitenizin{" "}
                      <code className="px-1 py-0.5 rounded bg-slate-100 text-slate-700 font-mono">
                        &lt;/body&gt;
                      </code>{" "}
                      etiketinden hemen önce yapıştırın.
                    </CardDescription>
                  </div>
                </div>
                <Badge variant="secondary" className="text-[11px] font-mono">
                  EMBED.JS
                </Badge>
              </div>
            </CardHeader>

            <CardContent className="space-y-4">
              {/* macOS Tarzı Koyu Kod Bloğu */}
              <div className="bg-slate-900 text-slate-100 rounded-2xl p-5 font-mono text-xs relative border border-slate-800 shadow-inner">
                <div className="flex items-center justify-between mb-3 pb-2.5 border-b border-slate-800">
                  <div className="flex items-center gap-1.5">
                    <span className="w-2.5 h-2.5 rounded-full bg-rose-500/80" />
                    <span className="w-2.5 h-2.5 rounded-full bg-amber-400/80" />
                    <span className="w-2.5 h-2.5 rounded-full bg-emerald-500/80" />
                    <span className="ml-2 text-[11px] text-slate-400 font-sans">
                      index.html
                    </span>
                  </div>

                  <Button
                    type="button"
                    size="sm"
                    onClick={copyEmbedCode}
                    className="h-7 px-2.5 rounded-lg bg-slate-800 hover:bg-slate-700 text-slate-100 text-[11px] font-sans"
                  >
                    {copiedEmbed ? (
                      <>
                        <Check className="w-3.5 h-3.5 mr-1 text-emerald-400" />
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

                <pre className="overflow-x-auto whitespace-pre-wrap break-all leading-relaxed text-violet-200 select-all">
                  {embedCode}
                </pre>
              </div>

              {/* 3 Adımlı Mini Kurulum Rehberi */}
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-2.5 pt-1">
                <div className="p-3 rounded-xl bg-slate-50 border border-slate-200/70">
                  <span className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-[#7c3aed]/10 text-[#7c3aed] text-[11px] font-bold mb-1.5">
                    1
                  </span>
                  <p className="text-xs font-semibold text-slate-800">
                    1. Kodu kopyalayın
                  </p>
                  <p className="text-[11px] text-slate-500 mt-0.5">
                    Tek tıkla script kodunu panonuza alın.
                  </p>
                </div>

                <div className="p-3 rounded-xl bg-slate-50 border border-slate-200/70">
                  <span className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-[#7c3aed]/10 text-[#7c3aed] text-[11px] font-bold mb-1.5">
                    2
                  </span>
                  <p className="text-xs font-semibold text-slate-800">
                    2. Sitenizin HTML/Layout dosyasına yapıştırın
                  </p>
                  <p className="text-[11px] text-slate-500 mt-0.5">
                    WordPress, Shopify, Next.js veya özel sitenize ekleyin.
                  </p>
                </div>

                <div className="p-3 rounded-xl bg-slate-50 border border-slate-200/70">
                  <span className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-[#7c3aed]/10 text-[#7c3aed] text-[11px] font-bold mb-1.5">
                    3
                  </span>
                  <p className="text-xs font-semibold text-slate-800">
                    3. Asistanınız sağ alt köşede anında aktif olur
                  </p>
                  <p className="text-[11px] text-slate-500 mt-0.5">
                    7/24 müşterilerinizi karşılamaya başlar.
                  </p>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Kart 2 — Direkt Paylaşım Linki (Web Sitesi Olmayanlar İçin) */}
          <Card className="rounded-2xl border-slate-200/80 shadow-sm">
            <CardHeader className="pb-4">
              <div className="flex items-start justify-between gap-4">
                <div className="flex items-center gap-2.5">
                  <div className="w-9 h-9 rounded-xl bg-orange-50 text-orange-600 flex items-center justify-center">
                    <Share2 className="w-5 h-5" />
                  </div>
                  <div>
                    <div className="flex items-center gap-2">
                      <CardTitle className="text-base">
                        Direkt Paylaşım Linki
                      </CardTitle>
                      <Badge
                        variant="secondary"
                        className={
                          directShareActive
                            ? "text-[10px] bg-emerald-50 text-emerald-700 border-emerald-200"
                            : "text-[10px]"
                        }
                      >
                        {directShareActive ? "Aktif" : "Pasif"}
                      </Badge>
                    </div>
                    <CardDescription className="text-xs mt-0.5">
                      Instagram biyografinizde, WhatsApp&apos;ta veya
                      e-postalarda paylaşabileceğiniz bağımsız tam ekran sohbet
                      sayfası.
                    </CardDescription>
                  </div>
                </div>

                <div className="flex items-center gap-2 shrink-0 pt-1">
                  <Switch
                    checked={directShareActive}
                    onCheckedChange={handleToggleDirectShare}
                    disabled={togglingShare}
                  />
                </div>
              </div>
            </CardHeader>

            <CardContent className="space-y-4">
              <div
                className={`bg-orange-50/70 border border-orange-200 text-orange-900 font-mono text-xs p-3.5 rounded-xl break-all transition-opacity ${
                  !directShareActive ? "opacity-50" : ""
                }`}
              >
                {fullShareUrl}
              </div>

              {!directShareActive && (
                <p className="text-xs text-amber-700 bg-amber-50 border border-amber-200/70 rounded-xl px-3 py-2 flex items-center gap-2">
                  <Lock className="w-3.5 h-3.5 shrink-0" />
                  Direkt paylaşım bağlantısı şu anda kapalı. Bu bağlantıyı açan
                  ziyaretçiler sohbet başlatamaz.
                </p>
              )}

              <div className="flex flex-wrap items-center gap-2">
                <Button
                  type="button"
                  onClick={copyShareUrl}
                  disabled={!directShareActive}
                  className="bg-slate-900 hover:bg-slate-800 text-white rounded-xl h-9 text-xs"
                >
                  {copiedShareLink ? (
                    <>
                      <Check className="w-3.5 h-3.5 mr-1.5 text-emerald-400" />
                      Link Kopyalandı
                    </>
                  ) : (
                    <>
                      <Copy className="w-3.5 h-3.5 mr-1.5" />
                      Linki Kopyala
                    </>
                  )}
                </Button>

                <Button
                  type="button"
                  variant="outline"
                  onClick={openLiveBotInNewTab}
                  disabled={!directShareActive}
                  className="rounded-xl h-9 text-xs border-slate-200"
                >
                  <ExternalLink className="w-3.5 h-3.5 mr-1.5" />
                  Yeni Sekmede Aç
                </Button>

                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => setConfirmRegenerateOpen(true)}
                  disabled={regenerating}
                  className="rounded-xl h-9 text-xs text-slate-600 hover:text-rose-600 hover:bg-rose-50 ml-auto"
                >
                  <RefreshCw
                    className={`w-3.5 h-3.5 mr-1.5 ${
                      regenerating ? "animate-spin" : ""
                    }`}
                  />
                  Anahtarı Yenile
                </Button>
              </div>
            </CardContent>
          </Card>
        </div>

        {/* SAĞ SÜTUN */}
        <div className="space-y-6">
          {/* Kart 3 — Domain Güvenliği ve Spam Koruması (İzinli Domain Listesi) */}
          <Card className="rounded-2xl border-slate-200/80 shadow-sm">
            <CardHeader className="pb-4">
              <div className="flex items-center gap-2.5">
                <div className="w-9 h-9 rounded-xl bg-emerald-50 text-emerald-600 flex items-center justify-center">
                  <ShieldCheck className="w-5 h-5" />
                </div>
                <div>
                  <CardTitle className="text-base">
                    İzinli Domain Listesi (CORS &amp; Güvenlik)
                  </CardTitle>
                  <CardDescription className="text-xs mt-0.5">
                    Widget&apos;ınızın başka sitelere kopyalanmasını veya spam
                    mesaj gelmesini önlemek için sadece izin verdiğiniz alan
                    adlarını yazın. (Boş bırakırsanız tüm sitelerde çalışır).
                  </CardDescription>
                </div>
              </div>
            </CardHeader>

            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label
                  htmlFor="allowed-domains"
                  className="text-xs font-semibold text-slate-700 flex items-center gap-1.5"
                >
                  <Globe className="w-3.5 h-3.5 text-slate-400" />
                  Yetkili Alan Adları (Virgül veya satır ile ayırın)
                </Label>
                <Textarea
                  id="allowed-domains"
                  rows={4}
                  value={allowedDomains}
                  onChange={(e) => setAllowedDomains(e.target.value)}
                  placeholder="ornek.com, magazam.com"
                  className="rounded-xl border-slate-200 bg-slate-50/50 focus:bg-white font-mono text-xs"
                />
                <p className="text-[11px] text-slate-400">
                  Örnek: <code className="font-mono">sirketim.com, app.sirketim.com</code>{" "}
                  — Alt alan adları otomatik olarak desteklenir.
                </p>
              </div>

              <Button
                type="button"
                onClick={handleSaveSecurity}
                disabled={saving}
                className="w-full bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-10 font-semibold shadow-sm"
              >
                {saving ? (
                  <>
                    <Loader2 className="w-4 h-4 mr-2 animate-spin" />
                    Kaydediliyor...
                  </>
                ) : (
                  <>
                    <ShieldCheck className="w-4 h-4 mr-2" />
                    Güvenlik Ayarlarını Kaydet
                  </>
                )}
              </Button>
            </CardContent>
          </Card>

          {/* Kart 4 — Aktif Görünüm Özeti */}
          <Card className="rounded-2xl border-slate-200/80 shadow-sm">
            <CardHeader className="pb-4">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2.5">
                  <div className="w-9 h-9 rounded-xl bg-violet-50 text-[#7c3aed] flex items-center justify-center">
                    <Sparkles className="w-5 h-5" />
                  </div>
                  <div>
                    <CardTitle className="text-base">
                      Aktif Görünüm Özeti
                    </CardTitle>
                    <CardDescription className="text-xs mt-0.5">
                      Ziyaretçilerinizin şu anda gördüğü canlı widget
                      yapılandırması.
                    </CardDescription>
                  </div>
                </div>
              </div>
            </CardHeader>

            <CardContent className="space-y-3.5">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div className="p-3.5 rounded-xl bg-slate-50 border border-slate-200/70">
                  <p className="text-[11px] font-medium text-slate-400 uppercase tracking-wider">
                    Widget Başlığı
                  </p>
                  <p className="text-sm font-semibold text-slate-900 mt-1 truncate">
                    {config?.widgetTitle || "Canlı Destek"}
                  </p>
                </div>

                <div className="p-3.5 rounded-xl bg-slate-50 border border-slate-200/70">
                  <p className="text-[11px] font-medium text-slate-400 uppercase tracking-wider">
                    Ana Renk
                  </p>
                  <div className="flex items-center gap-2 mt-1">
                    <span
                      className="w-4 h-4 rounded-full border border-black/10 shrink-0 shadow-sm"
                      style={{
                        backgroundColor: config?.primaryColor || "#7c3aed",
                      }}
                    />
                    <span className="text-sm font-mono font-semibold text-slate-900">
                      {config?.primaryColor || "#7c3aed"}
                    </span>
                  </div>
                </div>
              </div>

              <div className="p-3.5 rounded-xl bg-slate-50 border border-slate-200/70">
                <p className="text-[11px] font-medium text-slate-400 uppercase tracking-wider">
                  Karşılama Mesajı
                </p>
                <p className="text-xs text-slate-700 mt-1 leading-relaxed">
                  &ldquo;
                  {config?.welcomeMessage ||
                    "Merhaba, sağlıklı günler diliyorum."}
                  &rdquo;
                </p>
              </div>

              <div className="flex items-center justify-between p-3.5 rounded-xl bg-slate-50 border border-slate-200/70">
                <div>
                  <p className="text-xs font-semibold text-slate-800">
                    Daribase Rozeti (&ldquo;Powered by Daribase&rdquo;)
                  </p>
                  <p className="text-[11px] text-slate-500">
                    Sohbet penceresinin alt kısmındaki marka imzası
                  </p>
                </div>
                <Badge
                  variant="secondary"
                  className={
                    config?.showBadge === false
                      ? "bg-emerald-50 text-emerald-700 border-emerald-200"
                      : ""
                  }
                >
                  {config?.showBadge !== false ? "Görünür" : "Gizli"}
                </Badge>
              </div>

              {config?.publishableToken && (
                <div className="space-y-1.5 pt-1">
                  <Label className="text-[11px] text-slate-400 font-medium">
                    Kanal Yayın Anahtarı (Publishable Token)
                  </Label>
                  <Input
                    readOnly
                    value={config.publishableToken}
                    className="h-9 text-xs font-mono bg-slate-50 text-slate-600 rounded-xl"
                  />
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      {/* Anahtar Yenileme Onay Dialogu */}
      <Dialog
        open={confirmRegenerateOpen}
        onOpenChange={setConfirmRegenerateOpen}
      >
        <DialogContent className="rounded-2xl sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Paylaşım Anahtarını Yenile</DialogTitle>
            <DialogDescription className="text-xs leading-relaxed pt-1">
              Direkt paylaşım anahtarını yenilediğinizde daha önce
              paylaştığınız{" "}
              <span className="font-semibold text-slate-800">
                eski bağlantı anında geçersiz kalacaktır
              </span>
              . Devam etmek istediğinizden emin misiniz?
            </DialogDescription>
          </DialogHeader>
          <DialogFooter className="gap-2 sm:gap-0">
            <Button
              type="button"
              variant="outline"
              onClick={() => setConfirmRegenerateOpen(false)}
              disabled={regenerating}
              className="rounded-xl"
            >
              Vazgeç
            </Button>
            <Button
              type="button"
              onClick={handleRegenerateKey}
              disabled={regenerating}
              className="bg-rose-600 hover:bg-rose-700 text-white rounded-xl"
            >
              {regenerating ? (
                <>
                  <Loader2 className="w-4 h-4 mr-1.5 animate-spin" />
                  Yenileniyor...
                </>
              ) : (
                <>
                  <RefreshCw className="w-4 h-4 mr-1.5" />
                  Evet, Anahtarı Yenile
                </>
              )}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
