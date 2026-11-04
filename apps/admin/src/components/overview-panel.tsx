"use client";

import React, { useCallback, useEffect, useMemo, useState } from "react";
import { api, errorMessage } from "@/lib/api";
import type { OverviewDashboardData, PanelProps, Section } from "@/lib/types";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Activity,
  AlertCircle,
  AlertTriangle,
  ArrowRight,
  ArrowUpRight,
  BookOpen,
  Bot,
  Calendar,
  CheckCircle2,
  Clock,
  Globe,
  Inbox,
  Loader2,
  MessageSquare,
  Plug,
  RefreshCw,
  ShieldCheck,
  Sparkles,
  Timer,
  Users,
  Zap,
} from "lucide-react";

type PeriodKey = "24h" | "7d" | "30d" | "90d";

const PERIOD_OPTIONS: { key: PeriodKey; label: string }[] = [
  { key: "24h", label: "24 saat" },
  { key: "7d", label: "7 gün" },
  { key: "30d", label: "30 gün" },
  { key: "90d", label: "90 gün" },
];

export function OverviewPanel({
  workspaceId,
  onNavigate,
}: PanelProps & { onNavigate?: (section: Section) => void }) {
  const [period, setPeriod] = useState<PeriodKey>("7d");
  const [data, setData] = useState<OverviewDashboardData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadOverview = useCallback(
    async (selectedPeriod: PeriodKey, silent = false) => {
      if (!silent) {
        setLoading(true);
      }
      setError(null);
      try {
        const res = await api<OverviewDashboardData>(
          `/workspaces/${workspaceId}/overview?period=${selectedPeriod}`
        );
        setData(res);
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        if (!silent) {
          setLoading(false);
        }
      }
    },
    [workspaceId]
  );

  useEffect(() => {
    void loadOverview(period);
  }, [loadOverview, period]);

  const maxDailyTotal = useMemo(() => {
    if (!data?.dailyInteractions || data.dailyInteractions.length === 0) {
      return 1;
    }
    const maxVal = Math.max(...data.dailyInteractions.map((d) => d.total), 1);
    return maxVal;
  }, [data]);

  const handleNavigate = (target: Section | string) => {
    if (!onNavigate) return;
    if (target === "inbox") {
      onNavigate("conversations");
      return;
    }
    onNavigate(target as Section);
  };

  if (loading && !data) {
    return (
      <div className="p-6 max-w-[1400px] mx-auto flex items-center justify-center min-h-[460px]">
        <div className="flex flex-col items-center gap-3 text-slate-500">
          <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed]" />
          <p className="text-sm font-medium">
            Yönetim kokpiti verileri yükleniyor...
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-[1400px] mx-auto space-y-8">
      {error && (
        <Alert variant="destructive" className="rounded-2xl">
          <div className="flex items-center gap-2">
            <AlertCircle className="w-4 h-4 shrink-0" />
            <AlertDescription className="text-xs font-medium">
              {error}
            </AlertDescription>
          </div>
        </Alert>
      )}

      {/* 1. Üst Kokpit Kartı */}
      <div className="bg-white rounded-3xl border border-slate-200/80 p-6 space-y-6 shadow-sm">
        <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
          <div className="space-y-3">
            <span className="bg-orange-50 text-orange-600 border border-orange-200 text-[11px] font-semibold px-3 py-1 rounded-full inline-flex items-center gap-1.5">
              <Zap className="w-3.5 h-3.5" />
              YÖNETİM KOKPİTİ
            </span>

            <div>
              <h1 className="text-3xl font-bold text-slate-900 tracking-tight">
                Destek operasyonlarını tek ekranda yönetin
              </h1>
              <p className="text-sm text-slate-500 mt-1">
                Canlı sağlık durumu, risk ve AI kapsamasını tek komuta
                merkezinden takip edin.
              </p>
            </div>

            {/* Meta Bilgi Hapları */}
            <div className="flex flex-wrap items-center gap-2 pt-1">
              <span className="inline-flex items-center gap-1.5 text-xs font-medium text-slate-600 bg-slate-100/80 border border-slate-200/70 px-3 py-1 rounded-full">
                <Calendar className="w-3.5 h-3.5 text-slate-500" />
                Dönem: {data?.periodLabel || "7 gün"}
              </span>
              <span className="inline-flex items-center gap-1.5 text-xs font-medium text-slate-600 bg-slate-100/80 border border-slate-200/70 px-3 py-1 rounded-full">
                <Clock className="w-3.5 h-3.5 text-slate-500" />
                Veri aralığı: {data?.dateRangeLabel || "—"}
              </span>
              <span className="inline-flex items-center gap-1.5 text-xs font-medium text-slate-600 bg-slate-100/80 border border-slate-200/70 px-3 py-1 rounded-full">
                <RefreshCw className="w-3.5 h-3.5 text-slate-500" />
                Son güncelleme: {data?.lastUpdatedLabel || "Az önce"}
              </span>
            </div>
          </div>

          {/* Dönem Seçici Buton Grubu */}
          <div className="flex items-center gap-2 self-start">
            <div className="bg-slate-100 p-1 rounded-xl inline-flex gap-1">
              {PERIOD_OPTIONS.map((opt) => {
                const active = period === opt.key;
                return (
                  <button
                    key={opt.key}
                    type="button"
                    onClick={() => setPeriod(opt.key)}
                    className={`px-3 py-1.5 text-xs font-semibold rounded-lg transition-all cursor-pointer ${
                      active
                        ? "bg-white text-slate-900 shadow-xs"
                        : "text-slate-500 hover:text-slate-900"
                    }`}
                  >
                    {opt.label}
                  </button>
                );
              })}
            </div>

            <Button
              type="button"
              variant="outline"
              size="icon"
              onClick={() => void loadOverview(period)}
              title="Verileri Yenile"
              className="h-9 w-9 rounded-xl border-slate-200"
            >
              <RefreshCw
                className={`w-4 h-4 text-slate-600 ${
                  loading ? "animate-spin" : ""
                }`}
              />
            </Button>
          </div>
        </div>

        {/* 3'lü Ana Durum Kartı */}
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div className="rounded-2xl border border-slate-200/80 bg-slate-50/50 p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                HAZIRLIK SKORU
              </p>
              <div className="text-3xl font-extrabold text-slate-900">
                {data?.readinessScore ?? 0}%
              </div>
              <p className="text-xs text-slate-500 font-medium">
                {data?.readinessPassedCount ?? 0}/
                {data?.readinessTotalCount ?? 5} kontrol sağlam
              </p>
            </div>
            <div className="w-10 h-10 rounded-xl bg-teal-50 text-teal-600 border border-teal-200/70 flex items-center justify-center shrink-0">
              <ShieldCheck className="w-5 h-5" />
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200/80 bg-slate-50/50 p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                ÇÖZÜLMEMİŞ KUYRUK
              </p>
              <div className="text-3xl font-extrabold text-slate-900">
                {data?.unresolvedQueueCount ?? 0}
              </div>
              <p className="text-xs text-slate-500 font-medium">
                {data?.unassignedCount ?? 0} atamasız ·{" "}
                {data?.unansweredCount ?? 0} yanıtsız
              </p>
            </div>
            <div className="w-10 h-10 rounded-xl bg-teal-50 text-teal-600 border border-teal-200/70 flex items-center justify-center shrink-0">
              <Inbox className="w-5 h-5" />
            </div>
          </div>

          <div className="rounded-2xl border border-slate-200/80 bg-slate-50/50 p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                AI KAPSAMA
              </p>
              <div className="text-3xl font-extrabold text-slate-900">
                %{(data?.aiCoveragePercent ?? 100).toFixed(1)}
              </div>
              <p className="text-xs text-slate-500 font-medium">
                {data?.aiActiveConversationsText || "0/0 AI aktif sohbet"}
              </p>
            </div>
            <div className="w-10 h-10 rounded-xl bg-teal-50 text-teal-600 border border-teal-200/70 flex items-center justify-center shrink-0">
              <Bot className="w-5 h-5" />
            </div>
          </div>
        </div>
      </div>

      {/* 2. Performans Özeti */}
      <div className="space-y-3">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <div>
            <h2 className="text-lg font-bold text-slate-900">
              Performans Özeti
            </h2>
            <p className="text-xs text-slate-500">
              Seçili dönemin temel göstergeleri.
            </p>
          </div>
          <Button
            type="button"
            variant="outline"
            onClick={() => handleNavigate("conversations")}
            className="rounded-xl h-9 text-xs font-semibold border-slate-200 bg-white hover:bg-slate-50 self-start sm:self-auto"
          >
            <MessageSquare className="w-3.5 h-3.5 mr-1.5 text-[#7c3aed]" />
            Gelen kutusunu aç
          </Button>
        </div>

        {/* 4'lü KPI Grid */}
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardContent className="p-5 space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                  TOPLAM SOHBET
                </span>
                <Badge
                  variant="secondary"
                  className="bg-emerald-50 text-emerald-700 border-emerald-200 text-[10px]"
                >
                  Yeni
                </Badge>
              </div>
              <div className="text-3xl font-extrabold text-slate-900">
                {data?.totalConversations ?? 0}
              </div>
              <p className="text-[11px] text-slate-400">
                Seçili dönemde başlatılan oturumlar
              </p>
            </CardContent>
          </Card>

          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardContent className="p-5 space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                  AKTİF KULLANICI
                </span>
                <Badge
                  variant="secondary"
                  className="bg-emerald-50 text-emerald-700 border-emerald-200 text-[10px]"
                >
                  Yeni
                </Badge>
              </div>
              <div className="text-3xl font-extrabold text-slate-900">
                {data?.activeUsers ?? 0}
              </div>
              <p className="text-[11px] text-slate-400">
                Tekil ziyaretçi ve müşteri sayısı
              </p>
            </CardContent>
          </Card>

          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardContent className="p-5 space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                  AI BAŞARISI
                </span>
                <Badge
                  variant="secondary"
                  className="bg-emerald-50 text-emerald-700 border-emerald-200 text-[10px]"
                >
                  +100.0%
                </Badge>
              </div>
              <div className="text-3xl font-extrabold text-slate-900">
                %{(data?.aiSuccessRate ?? 100).toFixed(1)}
              </div>
              <p className="text-[11px] text-slate-400">
                Temsilci müdahalesiz çözülen oran
              </p>
            </CardContent>
          </Card>

          <Card className="rounded-2xl border-slate-200/80 shadow-xs">
            <CardContent className="p-5 space-y-2">
              <div className="flex items-center justify-between">
                <span className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                  ORTALAMA YANIT SÜRESİ
                </span>
                <Timer className="w-4 h-4 text-slate-400" />
              </div>
              <div className="text-3xl font-extrabold text-slate-900">
                {data?.avgResponseSeconds ?? 3.5}s
              </div>
              <p className="text-[11px] text-slate-400">
                AI anlık yanıt hızı ortalaması
              </p>
            </CardContent>
          </Card>
        </div>
      </div>

      {/* 3. Etkileşim ve Aktivite */}
      <div className="grid grid-cols-1 lg:grid-cols-[1fr_380px] gap-6">
        {/* Sol Kart: Etkileşim Analizi */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardHeader className="pb-2">
            <div className="flex items-center justify-between">
              <div>
                <CardTitle className="text-base font-bold text-slate-900">
                  Etkileşim Analizi
                </CardTitle>
                <CardDescription className="text-xs">
                  Son 7 gündeki bot ve insan etkileşimleri.
                </CardDescription>
              </div>
              <div className="flex items-center gap-3 text-xs text-slate-500">
                <span className="inline-flex items-center gap-1.5">
                  <span className="w-2.5 h-2.5 rounded-full bg-violet-600" />
                  Bot / Mesaj
                </span>
              </div>
            </div>
          </CardHeader>

          <CardContent className="pt-4">
            <div className="h-56 flex items-end justify-between gap-2 pt-6 px-2 border-b border-slate-100">
              {(data?.dailyInteractions ?? []).slice(-10).map((item, idx) => {
                const heightPercent = Math.max(
                  10,
                  Math.round((item.total / maxDailyTotal) * 100)
                );
                return (
                  <div
                    key={`${idx}-${item.dateLabel}`}
                    className="flex-1 flex flex-col items-center justify-end h-full group"
                  >
                    <span className="text-[10px] font-semibold text-slate-500 mb-1 opacity-80 group-hover:opacity-100">
                      {item.total}
                    </span>
                    <div className="w-full max-w-[38px] h-40 flex items-end justify-center bg-slate-50 rounded-t-full overflow-hidden p-0.5">
                      <div
                        style={{ height: `${heightPercent}%` }}
                        className="w-full bg-gradient-to-t from-violet-200 to-violet-600 rounded-t-full transition-all duration-300"
                      />
                    </div>
                  </div>
                );
              })}
            </div>

            <div className="flex items-center justify-between gap-2 pt-2.5 px-2">
              {(data?.dailyInteractions ?? []).slice(-10).map((item, idx) => (
                <div
                  key={`lbl-${idx}-${item.dateLabel}`}
                  className="flex-1 text-center text-[11px] font-medium text-slate-400 truncate"
                >
                  {item.dateLabel}
                </div>
              ))}
            </div>
          </CardContent>
        </Card>

        {/* Sağ Kart: Canlı Akış */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs flex flex-col">
          <CardHeader className="pb-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <span className="w-2.5 h-2.5 rounded-full bg-emerald-500 animate-pulse" />
                <CardTitle className="text-base font-bold text-slate-900">
                  Canlı Akış
                </CardTitle>
              </div>
              <Badge
                variant="secondary"
                className="bg-emerald-50 text-emerald-700 border-emerald-200 text-[10px] font-bold"
              >
                CANLI
              </Badge>
            </div>
            <CardDescription className="text-xs">
              Gerçek zamanlı son müşteri ve asistan mesajları.
            </CardDescription>
          </CardHeader>

          <CardContent className="flex-1 space-y-2.5 overflow-y-auto max-h-[260px]">
            {!data?.liveFeed || data.liveFeed.length === 0 ? (
              <div className="py-10 text-center space-y-2 text-slate-400">
                <Activity className="w-6 h-6 mx-auto stroke-1" />
                <p className="text-xs">Henüz canlı mesaj akışı bulunmuyor.</p>
              </div>
            ) : (
              data.liveFeed.map((feed) => (
                <div
                  key={feed.id}
                  onClick={() => handleNavigate("conversations")}
                  className="p-3 rounded-xl border border-slate-200/70 bg-slate-50/60 hover:bg-slate-100/70 transition-colors cursor-pointer space-y-1"
                >
                  <div className="flex items-center justify-between gap-2">
                    <div className="flex items-center gap-2 min-w-0">
                      <span
                        className={`w-2 h-2 rounded-full shrink-0 ${
                          feed.senderKind === "AI"
                            ? "bg-[#7c3aed]"
                            : feed.senderKind === "MEMBER"
                            ? "bg-blue-500"
                            : "bg-emerald-500"
                        }`}
                      />
                      <span className="text-xs font-semibold text-slate-800 truncate">
                        {feed.visitorName}
                      </span>
                    </div>
                    <span className="text-[11px] text-slate-400 shrink-0">
                      {feed.relativeTime}
                    </span>
                  </div>
                  <p className="text-xs text-slate-600 truncate pl-4">
                    {feed.preview}
                  </p>
                </div>
              ))
            )}
          </CardContent>
        </Card>
      </div>

      {/* 4. Sistem Durumu ve Kısayollar */}
      <div className="grid grid-cols-1 lg:grid-cols-[1fr_380px] gap-6">
        {/* Sol Kart: Sistem Durumu */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardHeader className="pb-3">
            <CardTitle className="text-base font-bold text-slate-900">
              Sistem Durumu
            </CardTitle>
            <CardDescription className="text-xs">
              Altyapı ve modül sağlık kontrolleri.
            </CardDescription>
          </CardHeader>

          <CardContent>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              {/* 1) Bilgi Bankası */}
              <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 flex items-center justify-between">
                <div>
                  <p className="text-xs font-semibold text-slate-800">
                    Bilgi Bankası
                  </p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    {data?.systemStatus.documentCount ?? 0} doküman
                  </p>
                </div>
                <CheckCircle2 className="w-5 h-5 text-emerald-600 shrink-0" />
              </div>

              {/* 2) Widget */}
              <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 flex items-center justify-between">
                <div>
                  <p className="text-xs font-semibold text-slate-800">Widget</p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    {data?.systemStatus.widgetLive !== false
                      ? "Yayında"
                      : "Pasif"}
                  </p>
                </div>
                <CheckCircle2 className="w-5 h-5 text-emerald-600 shrink-0" />
              </div>

              {/* 3) Kanallar */}
              <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 flex items-center justify-between">
                <div>
                  <p className="text-xs font-semibold text-slate-800">
                    Kanallar
                  </p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    {(data?.systemStatus.connectedChannelsCount ?? 0) > 1
                      ? "Bağlı"
                      : "Bağlantı yok"}
                  </p>
                </div>
                {(data?.systemStatus.connectedChannelsCount ?? 0) > 1 ? (
                  <CheckCircle2 className="w-5 h-5 text-emerald-600 shrink-0" />
                ) : (
                  <AlertTriangle className="w-5 h-5 text-amber-500 shrink-0" />
                )}
              </div>

              {/* 4) Ekip */}
              <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 flex items-center justify-between">
                <div>
                  <p className="text-xs font-semibold text-slate-800">Ekip</p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    {data?.systemStatus.memberCount ?? 1} üye
                  </p>
                </div>
                <CheckCircle2 className="w-5 h-5 text-emerald-600 shrink-0" />
              </div>

              {/* 5) AI Kullanımı */}
              <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 flex items-center justify-between md:col-span-2">
                <div>
                  <p className="text-xs font-semibold text-slate-800">
                    AI Kullanımı
                  </p>
                  <p className="text-xs text-slate-500 mt-0.5">
                    ~{data?.systemStatus.aiMessagesUsed ?? 0} mesaj
                  </p>
                </div>
                <CheckCircle2 className="w-5 h-5 text-emerald-600 shrink-0" />
              </div>
            </div>
          </CardContent>
        </Card>

        {/* Sağ Kart: Hızlı Aksiyonlar */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardHeader className="pb-3">
            <CardTitle className="text-base font-bold text-slate-900">
              Hızlı Aksiyonlar
            </CardTitle>
            <CardDescription className="text-xs">
              Sık kullanılan yönetim ekranlarına doğrudan geçiş yapın.
            </CardDescription>
          </CardHeader>

          <CardContent className="space-y-2.5">
            {[
              {
                label: "Canlı sohbetlere git",
                section: "conversations" as Section,
                icon: MessageSquare,
              },
              {
                label: "Widget & Bilgi Bankası",
                section: "knowledge" as Section,
                icon: BookOpen,
              },
              {
                label: "Kanal entegrasyonları",
                section: "channels" as Section,
                icon: Globe,
              },
              {
                label: "Ekip & Rol Yönetimi",
                section: "members" as Section,
                icon: Users,
              },
            ].map((item) => {
              const Icon = item.icon;
              return (
                <button
                  key={item.label}
                  type="button"
                  onClick={() => handleNavigate(item.section)}
                  className="w-full p-3.5 rounded-xl border border-slate-200/80 bg-slate-50/60 hover:bg-violet-50/60 hover:border-violet-200 flex items-center justify-between transition-all cursor-pointer group"
                >
                  <div className="flex items-center gap-2.5">
                    <Icon className="w-4 h-4 text-slate-500 group-hover:text-[#7c3aed]" />
                    <span className="text-xs font-semibold text-slate-800 group-hover:text-slate-900">
                      {item.label}
                    </span>
                  </div>
                  <ArrowUpRight className="w-4 h-4 text-slate-400 group-hover:text-[#7c3aed]" />
                </button>
              );
            })}
          </CardContent>
        </Card>
      </div>

      {/* 5. Operasyon ve Kanallar (Omnichannel Performansı) */}
      <Card className="rounded-2xl border-slate-200/80 shadow-xs">
        <CardHeader className="pb-2">
          <CardTitle className="text-base font-bold text-slate-900">
            Omnichannel Performansı
          </CardTitle>
          <CardDescription className="text-xs">
            Bağlı mesajlaşma kanallarının dağılımı ve yanıt performansı.
          </CardDescription>
        </CardHeader>
        <CardContent className="py-10 flex flex-col items-center justify-center text-center space-y-3">
          <div className="w-12 h-12 rounded-2xl bg-slate-100 text-slate-500 flex items-center justify-center">
            <Plug className="w-6 h-6" />
          </div>
          <div className="space-y-1">
            <p className="text-sm font-semibold text-slate-800">
              Henüz kanal aktivitesi yok.
            </p>
            <p className="text-xs text-slate-500 max-w-md">
              Web widget dışında WhatsApp, Instagram ve diğer müşteri
              kanallarınızı bağlayarak tüm mesajları tek merkezden yönetin.
            </p>
          </div>
          <Button
            type="button"
            onClick={() => handleNavigate("channels")}
            className="bg-slate-900 hover:bg-slate-800 text-white rounded-xl h-9 text-xs font-semibold px-4"
          >
            Kanal bağla
            <ArrowRight className="w-3.5 h-3.5 ml-1.5" />
          </Button>
        </CardContent>
      </Card>

      {/* Bilgi ve Öneriler */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Sol: Bilgi Bankası & Eğitim Akışı */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardHeader className="pb-3">
            <CardTitle className="text-base font-bold text-slate-900">
              Bilgi Bankası &amp; Eğitim Akışı
            </CardTitle>
            <CardDescription className="text-xs">
              AI asistanınızın beslendiği aktif veri kaynakları.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div className="p-4 rounded-2xl border border-slate-200/80 bg-slate-50/60 space-y-1">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-400">
                DOKÜMANLAR
              </p>
              <div className="text-2xl font-extrabold text-slate-900">
                {data?.systemStatus.documentCount ?? 0}
              </div>
              <p className="text-xs text-slate-500">Eğitime hazır kaynak</p>
            </div>

            <div className="p-4 rounded-2xl border border-slate-200/80 bg-slate-50/60 space-y-1">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-400">
                CRAWL DURUMU
              </p>
              <div className="text-base font-bold text-emerald-700 flex items-center gap-1.5 pt-1">
                <Sparkles className="w-4 h-4 text-emerald-600" />
                Senkronize
              </div>
              <p className="text-xs text-slate-500">
                Tüm indeksler güncel durumda
              </p>
            </div>
          </CardContent>
        </Card>

        {/* Sağ: Önerilen Aksiyonlar */}
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardHeader className="pb-3">
            <CardTitle className="text-base font-bold text-slate-900">
              Önerilen Aksiyonlar
            </CardTitle>
            <CardDescription className="text-xs">
              Asistan performansını artırmak için önerilen iyileştirmeler.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-2.5">
            {!data?.recommendations || data.recommendations.length === 0 ? (
              <div className="p-4 rounded-xl border border-emerald-200 bg-emerald-50/60 flex items-center gap-3 text-emerald-900 text-xs font-medium">
                <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
                Tüm önerilen kurulum ve kalite adımları tamamlandı!
              </div>
            ) : (
              data.recommendations.map((rec, index) => (
                <button
                  key={`${index}-${rec.title}`}
                  type="button"
                  onClick={() => handleNavigate(rec.targetSection)}
                  className="w-full text-left p-3.5 rounded-xl border border-amber-200/80 bg-amber-50/50 hover:bg-amber-50 transition-colors flex items-start justify-between gap-3 cursor-pointer"
                >
                  <div className="flex items-start gap-2.5">
                    <AlertTriangle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                    <div>
                      <p className="text-xs font-bold text-slate-900">
                        {rec.title}
                      </p>
                      <p className="text-xs text-slate-600 mt-0.5">
                        {rec.description}
                      </p>
                    </div>
                  </div>
                  <ArrowUpRight className="w-4 h-4 text-amber-700 shrink-0 mt-0.5" />
                </button>
              ))
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
