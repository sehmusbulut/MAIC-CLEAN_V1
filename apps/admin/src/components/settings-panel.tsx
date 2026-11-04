"use client";

import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from "react";
import {
  AlertCircle,
  ArrowLeft,
  ArrowRight,
  Briefcase,
  Check,
  CheckCircle2,
  Copy,
  Image as ImageIcon,
  RotateCcw,
  Send,
  Sparkles,
  Trash2,
  Volume2,
} from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { api, apiUpload, errorMessage } from "@/lib/api";
import type {
  PanelProps,
  PlaygroundChatMessage,
  PlaygroundChatResponse,
  WizardCatalogOption,
  WorkspaceSettingsOverview,
} from "@/lib/types";

const DEFAULT_USE_CASES: WizardCatalogOption[] = [
  {
    id: "uc-1",
    category: "USE_CASE",
    key: "ecommerce",
    title: "Satış / E-ticaret",
    description: "Sipariş, kargo, iade ve ürün sorularını otomatik yanıtla.",
    iconName: "ShoppingBag",
  },
  {
    id: "uc-2",
    category: "USE_CASE",
    key: "education",
    title: "Eğitim / Üniversite",
    description: "Kayıt, program, burs ve kampüs sorularını yanıtla.",
    iconName: "GraduationCap",
  },
  {
    id: "uc-3",
    category: "USE_CASE",
    key: "health_tourism",
    title: "Sağlık Turizmi",
    description: "Tedavi süreçleri, randevu ve ön bilgi taleplerini karşıla.",
    iconName: "HeartPulse",
  },
  {
    id: "uc-4",
    category: "USE_CASE",
    key: "saas",
    title: "Yazılım / Hizmet",
    description: "Teknik destek, abonelik ve entegrasyon sorularını çöz.",
    iconName: "Code2",
  },
  {
    id: "uc-5",
    category: "USE_CASE",
    key: "general",
    title: "Genel Kurum / İşletme",
    description: "Kurumsal iletişim ve sık sorulan müşteri sorularını yönet.",
    iconName: "Building2",
  },
];

const DEFAULT_ASSISTANT_ROLES: WizardCatalogOption[] = [
  {
    id: "ar-1",
    category: "ASSISTANT_ROLE",
    key: "presales",
    title: "Satış öncesi soruları cevaplasın",
    description: "Müşteriyi ikna etsin, ürün/hizmet avantajlarını anlatsın.",
    iconName: "Sparkles",
  },
  {
    id: "ar-2",
    category: "ASSISTANT_ROLE",
    key: "helpdesk",
    title: "Bilgi masası gibi çalışsın",
    description: "Bilgi bankasındaki kurallara göre net ve doğru bilgi versin.",
    iconName: "BookOpen",
  },
  {
    id: "ar-3",
    category: "ASSISTANT_ROLE",
    key: "leadgen",
    title: "Lead toplasın ve filtrelesin",
    description: "İletişim bilgilerini ve talebi alıp satış ekibine hazırlasın.",
    iconName: "Users",
  },
];

const DEFAULT_PRIMARY_CHANNELS: WizardCatalogOption[] = [
  {
    id: "pc-1",
    category: "PRIMARY_CHANNEL",
    key: "website",
    title: "Web sitesi",
    description: "Sitenizdeki sağ alt sohbet balonu.",
    iconName: "Globe",
  },
  {
    id: "pc-2",
    category: "PRIMARY_CHANNEL",
    key: "instagram",
    title: "Instagram / DM",
    description: "Sosyal medya mesaj trafiği.",
    iconName: "Instagram",
  },
  {
    id: "pc-3",
    category: "PRIMARY_CHANNEL",
    key: "whatsapp",
    title: "WhatsApp",
    description: "Hızlı mesajlaşma hattı.",
    iconName: "MessageCircle",
  },
  {
    id: "pc-4",
    category: "PRIMARY_CHANNEL",
    key: "marketplace",
    title: "Pazar yeri",
    description: "Mağaza ve ürün soruları.",
    iconName: "Store",
  },
  {
    id: "pc-5",
    category: "PRIMARY_CHANNEL",
    key: "omnichannel",
    title: "Birden fazla kanal",
    description: "Tüm kanallardan ortak yönetim.",
    iconName: "Share2",
  },
  {
    id: "pc-6",
    category: "PRIMARY_CHANNEL",
    key: "other",
    title: "Diğer",
    description: "Özel entegrasyon veya direkt paylaşım linki.",
    iconName: "MoreHorizontal",
  },
];

const DEFAULT_KNOWLEDGE_METHODS: WizardCatalogOption[] = [
  {
    id: "km-1",
    category: "KNOWLEDGE_METHOD",
    key: "manual_prompt",
    title: "Ben kısa kısa anlatacağım",
    description: "İş özeti ve sık sorulan soruları kendim yazacağım.",
    iconName: "PenTool",
  },
  {
    id: "km-2",
    category: "KNOWLEDGE_METHOD",
    key: "website_crawl",
    title: "Web sitemden alacağım",
    description: "Web sitemin linkini ekleyip içeriği taratacağım.",
    iconName: "Globe",
  },
  {
    id: "km-3",
    category: "KNOWLEDGE_METHOD",
    key: "file_upload",
    title: "Dosya ve doküman yükleyeceğim",
    description: "PDF, Word veya metin belgelerimi yükleyeceğim.",
    iconName: "FileText",
  },
];

const SCHEDULE_LABELS: Record<string, string> = {
  ALWAYS_AI: "Her zaman AI",
  OFF_HOURS_AI: "Mesai saatleri dışında",
  HUMAN_ONLY: "Sadece insan",
};

const TONE_LABELS: Record<string, string> = {
  CLASSIC: "Klasik",
  MODERN: "Modern",
};

function playTestTone(tone: string) {
  try {
    const AudioCtx = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    const ctx = new AudioCtx();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = tone === "MODERN" ? "triangle" : "sine";
    osc.frequency.setValueAtTime(tone === "MODERN" ? 660 : 520, ctx.currentTime);
    osc.frequency.exponentialRampToValueAtTime(
      tone === "MODERN" ? 880 : 640,
      ctx.currentTime + 0.18
    );
    gain.gain.setValueAtTime(0.15, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.25);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + 0.25);
  } catch {
    // ignore audio block
  }
}

export function SettingsPanel({ workspaceId, onSessionRefresh }: PanelProps) {
  const [step, setStep] = useState<1 | 2 | 3 | 4>(1);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [uploadingLogo, setUploadingLogo] = useState(false);

  // Settings form state
  const [isTrained, setIsTrained] = useState(false);
  const [canRemoveBadge, setCanRemoveBadge] = useState(false);
  const [brandName, setBrandName] = useState("");
  const [widgetTitle, setWidgetTitle] = useState("Canlı Destek");
  const [widgetSubtitle, setWidgetSubtitle] = useState("Genel destek • Online");
  const [primaryColor, setPrimaryColor] = useState("#7c3aed");
  const [logoUrl, setLogoUrl] = useState<string | null>(null);
  const [showBadge, setShowBadge] = useState(true);

  const [useCase, setUseCase] = useState("ecommerce");
  const [assistantRole, setAssistantRole] = useState("presales");
  const [primaryChannel, setPrimaryChannel] = useState("website");
  const [initialKnowledgeMethod, setInitialKnowledgeMethod] = useState("manual_prompt");

  const [businessSummary, setBusinessSummary] = useState("");
  const [welcomeMessage, setWelcomeMessage] = useState("Merhaba, sağlıklı günler diliyorum.");
  const [keyFaq, setKeyFaq] = useState("");
  const [answerBoundaries, setAnswerBoundaries] = useState("");
  const [escalationRules, setEscalationRules] = useState("");

  const [aiScheduleMode, setAiScheduleMode] = useState("ALWAYS_AI");
  const [soundNotificationEnabled, setSoundNotificationEnabled] = useState(true);
  const [soundTone, setSoundTone] = useState("MODERN");
  const [allowedDomains, setAllowedDomains] = useState("");
  const [directShareActive, setDirectShareActive] = useState(true);
  const [directShareKey, setDirectShareKey] = useState("");

  const [readiness, setReadiness] = useState<{
    profileScore: number;
    faqSeedCount: number;
    sourceCount: number;
    strengths: string[];
    missingPoints: string[];
  }>({
    profileScore: 50,
    faqSeedCount: 0,
    sourceCount: 0,
    strengths: [],
    missingPoints: [],
  });

  const [catalog, setCatalog] = useState<{
    useCases: WizardCatalogOption[];
    assistantRoles: WizardCatalogOption[];
    primaryChannels: WizardCatalogOption[];
    knowledgeMethods: WizardCatalogOption[];
  }>({
    useCases: DEFAULT_USE_CASES,
    assistantRoles: DEFAULT_ASSISTANT_ROLES,
    primaryChannels: DEFAULT_PRIMARY_CHANNELS,
    knowledgeMethods: DEFAULT_KNOWLEDGE_METHODS,
  });

  // Playground state
  const [usedPlayground, setUsedPlayground] = useState(0);
  const [playgroundCap, setPlaygroundCap] = useState(200);
  const [chatHistory, setChatHistory] = useState<PlaygroundChatMessage[]>([]);
  const [chatInput, setChatInput] = useState("");
  const [chatBusy, setChatBusy] = useState(false);

  // Feedback & copy state
  const [feedback, setFeedback] = useState<{ text: string; type: "success" | "error" } | null>(
    null
  );
  const [copiedField, setCopiedField] = useState<"embed" | "link" | null>(null);

  const logoInputRef = useRef<HTMLInputElement | null>(null);
  const chatScrollRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!feedback) return;
    const timer = setTimeout(() => setFeedback(null), 4000);
    return () => clearTimeout(timer);
  }, [feedback]);

  const applySettingsResponse = useCallback((res: WorkspaceSettingsOverview) => {
    setIsTrained(Boolean(res.isTrained));
    setCanRemoveBadge(Boolean(res.canRemoveBadge));
    setBrandName(res.brandName ?? "");
    setWidgetTitle(res.widgetTitle || "Canlı Destek");
    setWidgetSubtitle(res.widgetSubtitle || "Genel destek • Online");
    setPrimaryColor(res.primaryColor || "#7c3aed");
    setLogoUrl(res.logoUrl ?? null);
    setShowBadge(res.showBadge ?? true);
    setUseCase(res.useCase || "ecommerce");
    setAssistantRole(res.assistantRole || "presales");
    setPrimaryChannel(res.primaryChannel || "website");
    setInitialKnowledgeMethod(res.initialKnowledgeMethod || "manual_prompt");
    setBusinessSummary(res.businessSummary ?? "");
    setWelcomeMessage(res.welcomeMessage || "Merhaba, sağlıklı günler diliyorum.");
    setKeyFaq(res.keyFaq ?? "");
    setAnswerBoundaries(res.answerBoundaries ?? "");
    setEscalationRules(res.escalationRules ?? "");
    setAiScheduleMode(res.aiScheduleMode || "ALWAYS_AI");
    setSoundNotificationEnabled(res.soundNotificationEnabled ?? true);
    setSoundTone(res.soundTone || "MODERN");
    setAllowedDomains(res.allowedDomains ?? "");
    setDirectShareActive(res.directShareActive ?? true);
    setDirectShareKey(res.directShareKey ?? "");
    setUsedPlayground(res.usedPlaygroundMessagesThisMonth ?? 0);
    setPlaygroundCap(res.playgroundMonthlyCap || 200);

    if (res.readiness) {
      setReadiness({
        profileScore: res.readiness.profileScore ?? 0,
        faqSeedCount: res.readiness.faqSeedCount ?? 0,
        sourceCount: res.readiness.sourceCount ?? 0,
        strengths: res.readiness.strengths ?? [],
        missingPoints: res.readiness.missingPoints ?? [],
      });
    }

    if (res.catalog) {
      setCatalog({
        useCases:
          res.catalog.useCases?.length > 0 ? res.catalog.useCases : DEFAULT_USE_CASES,
        assistantRoles:
          res.catalog.assistantRoles?.length > 0
            ? res.catalog.assistantRoles
            : DEFAULT_ASSISTANT_ROLES,
        primaryChannels:
          res.catalog.primaryChannels?.length > 0
            ? res.catalog.primaryChannels
            : DEFAULT_PRIMARY_CHANNELS,
        knowledgeMethods:
          res.catalog.knowledgeMethods?.length > 0
            ? res.catalog.knowledgeMethods
            : DEFAULT_KNOWLEDGE_METHODS,
      });
    }
  }, []);

  useEffect(() => {
    let active = true;
    setLoading(true);
    api<WorkspaceSettingsOverview>(`/workspaces/${workspaceId}/settings`)
      .then((res) => {
        if (!active) return;
        applySettingsResponse(res);
      })
      .catch((err) => {
        if (!active) return;
        setFeedback({ text: errorMessage(err), type: "error" });
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [workspaceId, applySettingsResponse]);

  useEffect(() => {
    chatScrollRef.current?.scrollTo({
      top: chatScrollRef.current.scrollHeight,
      behavior: "smooth",
    });
  }, [chatHistory, welcomeMessage]);

  async function saveSettings(advanceStep = false) {
    if (saving) return;
    setSaving(true);
    setFeedback(null);
    try {
      const updated = await api<WorkspaceSettingsOverview>(
        `/workspaces/${workspaceId}/settings`,
        {
          method: "PUT",
          body: JSON.stringify({
            brandName,
            widgetTitle,
            widgetSubtitle,
            primaryColor,
            showBadge: canRemoveBadge ? showBadge : true,
            directShareActive,
            useCase,
            assistantRole,
            primaryChannel,
            initialKnowledgeMethod,
            welcomeMessage,
            businessSummary,
            keyFaq,
            answerBoundaries,
            escalationRules,
            aiScheduleMode,
            soundNotificationEnabled,
            soundTone,
            allowedDomains,
          }),
        }
      );
      applySettingsResponse(updated);
      onSessionRefresh?.();
      if (advanceStep && step < 4) {
        setStep((prev) => (prev < 4 ? ((prev + 1) as 1 | 2 | 3 | 4) : prev));
      } else {
        setFeedback({ text: "Sihirbaz ayarları başarıyla kaydedildi.", type: "success" });
      }
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setSaving(false);
    }
  }

  async function handleLogoUpload(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file || uploadingLogo) return;

    setUploadingLogo(true);
    setFeedback(null);
    try {
      const res = await apiUpload<{ logoUrl: string | null }>(
        `/workspaces/${workspaceId}/settings/logo`,
        file
      );
      setLogoUrl(res.logoUrl);
      setFeedback({ text: "Marka logosu güncellendi.", type: "success" });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setUploadingLogo(false);
    }
  }

  async function handleLogoDelete() {
    if (uploadingLogo) return;
    setUploadingLogo(true);
    setFeedback(null);
    try {
      await api<{ logoUrl: string | null }>(`/workspaces/${workspaceId}/settings/logo`, {
        method: "DELETE",
      });
      setLogoUrl(null);
      setFeedback({ text: "Logo kaldırıldı.", type: "success" });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setUploadingLogo(false);
    }
  }

  async function handleSendPlayground(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const trimmed = chatInput.trim();
    if (!trimmed || chatBusy) return;

    const previousHistory = [...chatHistory];
    const nextHistory: PlaygroundChatMessage[] = [
      ...previousHistory,
      { role: "user", content: trimmed },
    ];
    setChatHistory(nextHistory);
    setChatInput("");
    setChatBusy(true);

    try {
      const res = await api<PlaygroundChatResponse>(
        `/workspaces/${workspaceId}/playground/chat`,
        {
          method: "POST",
          body: JSON.stringify({
            message: trimmed,
            history: previousHistory,
          }),
        }
      );
      setChatHistory([
        ...nextHistory,
        { role: "assistant", content: res.reply },
      ]);
      setUsedPlayground(res.usedPlaygroundMessages);
      setPlaygroundCap(res.playgroundMonthlyCap);
      if (soundNotificationEnabled) {
        playTestTone(soundTone);
      }
    } catch (err) {
      setChatHistory([
        ...nextHistory,
        { role: "assistant", content: `Uyarı: ${errorMessage(err)}` },
      ]);
    } finally {
      setChatBusy(false);
    }
  }

  function handleSelectUseCase(option: WizardCatalogOption) {
    setUseCase(option.key);
    if (!welcomeMessage.trim() && option.defaultWelcomeMessage) {
      setWelcomeMessage(option.defaultWelcomeMessage);
    }
    if (!businessSummary.trim() && option.defaultBusinessSummary) {
      setBusinessSummary(option.defaultBusinessSummary);
    }
    if (!keyFaq.trim() && option.defaultKeyFaq) {
      setKeyFaq(option.defaultKeyFaq);
    }
  }

  function copyText(text: string, type: "embed" | "link") {
    void navigator.clipboard.writeText(text);
    setCopiedField(type);
    setTimeout(() => setCopiedField(null), 2000);
  }

  const embedCode = `<script src="https://daribase.com/embed.js" data-workspace-id="${workspaceId}"></script>`;
  const origin = typeof window !== "undefined" ? window.location.origin : "https://daribase.com";
  const directShareUrl = `${origin}/c/${workspaceId}?key=${directShareKey}`;

  if (loading) {
    return (
      <div className="p-6">
        <Card>
          <CardContent className="p-10 text-center text-sm text-slate-500">
            Widget kurulum sihirbazı yükleniyor…
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 lg:grid-cols-[430px_1fr] h-[calc(100vh-3.5rem)] overflow-hidden">
      {/* SOL SÜTUN: 4 ADIMLI KAYDIRILABİLİR SİHİRBAZ */}
      <div className="bg-white border-r border-slate-200 flex flex-col h-full overflow-hidden">
        {/* 1. Üst Sabit Başlık */}
        <div className="px-5 py-3.5 border-b border-slate-100 flex items-center justify-between shrink-0">
          <div>
            <div className="font-semibold text-sm text-slate-900">Chat Playground</div>
            <div className="text-xs text-slate-500">Widget kurulum sihirbazı</div>
          </div>
          <span className="bg-emerald-50 text-emerald-700 border border-emerald-200 text-xs px-2.5 py-0.5 rounded-full font-medium">
            {isTrained ? "• Eğitildi" : "• Hazır"}
          </span>
        </div>

        {/* 2. Orta Kaydırılabilir Alan */}
        <div className="flex-1 overflow-y-auto p-4 space-y-4">
          {/* Küçük adım seçici */}
          <div className="grid grid-cols-4 gap-1.5 bg-slate-100 p-1 rounded-xl">
            {([1, 2, 3, 4] as const).map((num) => (
              <button
                key={num}
                type="button"
                onClick={() => setStep(num)}
                className={`py-1.5 text-xs font-semibold rounded-lg transition-all ${
                  step === num
                    ? "bg-white text-slate-900 shadow-xs"
                    : "text-slate-500 hover:text-slate-900"
                }`}
              >
                Adım {num}/4
              </button>
            ))}
          </div>

          {feedback && (
            <Alert
              variant={feedback.type === "error" ? "destructive" : "default"}
              className={
                feedback.type === "error"
                  ? "border-rose-200 bg-rose-50 text-rose-700 py-2.5"
                  : "border-emerald-200 bg-emerald-50 text-emerald-800 py-2.5"
              }
            >
              <AlertDescription className="text-xs font-medium flex items-center gap-2">
                {feedback.type === "error" ? (
                  <AlertCircle className="h-4 w-4 shrink-0" />
                ) : (
                  <CheckCircle2 className="h-4 w-4 shrink-0" />
                )}
                <span>{feedback.text}</span>
              </AlertDescription>
            </Alert>
          )}

          {/* ADIM 1/4 — Markalama */}
          {step === 1 && (
            <div className="space-y-4">
              <div>
                <h3 className="text-sm font-bold text-slate-900">
                  Adım 1/4 — Markalama (Logo ve renk tonu)
                </h3>
                <p className="text-xs text-slate-500">
                  Asistanınızın görünümünü, sektörünü ve görevini seçin.
                </p>
              </div>

              {/* Marka Kimliği */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-4">
                <div className="text-xs font-semibold uppercase tracking-wider text-slate-500">
                  Marka Kimliği (Logo ve Başlık)
                </div>

                <div className="flex items-center gap-4">
                  <div className="h-16 w-16 rounded-2xl border-2 border-dashed border-slate-300 bg-slate-50 flex items-center justify-center overflow-hidden shrink-0">
                    {logoUrl ? (
                      // eslint-disable-next-line @next/next/no-img-element
                      <img
                        src={logoUrl}
                        alt="Marka Logosu"
                        className="h-full w-full object-contain"
                      />
                    ) : (
                      <ImageIcon className="h-6 w-6 text-slate-400" />
                    )}
                  </div>
                  <div className="space-y-1.5">
                    <input
                      ref={logoInputRef}
                      type="file"
                      accept="image/png,image/jpeg,image/webp,image/svg+xml"
                      onChange={(e) => void handleLogoUpload(e)}
                      className="hidden"
                    />
                    <div className="flex items-center gap-2">
                      <Button
                        type="button"
                        size="sm"
                        disabled={uploadingLogo}
                        onClick={() => logoInputRef.current?.click()}
                        className="bg-slate-900 hover:bg-slate-800 text-white"
                      >
                        {uploadingLogo ? "Yükleniyor…" : "Logo Yükle"}
                      </Button>
                      {logoUrl && (
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          disabled={uploadingLogo}
                          onClick={() => void handleLogoDelete()}
                          className="text-rose-600"
                        >
                          <Trash2 className="h-3.5 w-3.5" />
                        </Button>
                      )}
                    </div>
                    <p className="text-[11px] text-slate-500">
                      PNG, JPG, WEBP veya SVG (Maks. 2MB)
                    </p>
                  </div>
                </div>

                <div className="space-y-3">
                  <div className="space-y-1">
                    <Label className="text-[11px] uppercase tracking-wider text-slate-500">
                      MARKA (ŞİRKET) ADI
                    </Label>
                    <Input
                      value={brandName}
                      onChange={(e) => setBrandName(e.target.value)}
                      placeholder="Örn: Daribase"
                    />
                  </div>
                  <div className="space-y-1">
                    <Label className="text-[11px] uppercase tracking-wider text-slate-500">
                      WİDGET BAŞLIĞI
                    </Label>
                    <Input
                      value={widgetTitle}
                      onChange={(e) => setWidgetTitle(e.target.value)}
                      placeholder="Canlı Destek"
                    />
                  </div>
                </div>
              </div>

              {/* Renk Paleti */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-3">
                <div className="text-xs font-semibold uppercase tracking-wider text-slate-500">
                  Renk Paleti (Ana Renk Tonu)
                </div>
                <div className="flex items-center gap-3">
                  <input
                    type="color"
                    value={primaryColor}
                    onChange={(e) => setPrimaryColor(e.target.value)}
                    className="h-10 w-10 cursor-pointer rounded-full border border-slate-200 bg-transparent p-0.5"
                    aria-label="Ana renk seçici"
                  />
                  <Input
                    value={primaryColor}
                    onChange={(e) => setPrimaryColor(e.target.value)}
                    placeholder="#7c3aed"
                    className="font-mono uppercase"
                  />
                </div>
              </div>

              {/* Daribase Rozeti */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <div className="text-xs font-semibold text-slate-900">
                      &quot;Powered by Daribase&quot; imzasını göster
                    </div>
                    {!canRemoveBadge && (
                      <div className="text-[11px] text-amber-600 mt-0.5">
                        İmzayı gizlemek için Pro veya Scale pakete yükseltin.
                      </div>
                    )}
                  </div>
                  <Switch
                    checked={canRemoveBadge ? showBadge : true}
                    disabled={!canRemoveBadge}
                    onCheckedChange={(checked) => setShowBadge(checked)}
                  />
                </div>
              </div>

              {/* Kullanım Alanı */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="text-xs font-semibold text-slate-900">
                  Bu asistanı hangi yapı için kuruyorsun?
                </div>
                <div className="grid grid-cols-1 gap-2">
                  {catalog.useCases.map((item) => {
                    const selected = useCase === item.key;
                    return (
                      <button
                        key={item.key}
                        type="button"
                        onClick={() => handleSelectUseCase(item)}
                        className={`w-full text-left rounded-xl border p-3 transition-all ${
                          selected
                            ? "bg-slate-900 text-white border-slate-900 shadow-xs"
                            : "bg-white text-slate-800 border-slate-200 hover:bg-slate-50"
                        }`}
                      >
                        <div className="text-xs font-semibold">{item.title}</div>
                        <div
                          className={`text-[11px] mt-0.5 ${
                            selected ? "text-slate-300" : "text-slate-500"
                          }`}
                        >
                          {item.description}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>

              {/* Asistanın Rolü */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="text-xs font-semibold text-slate-900">
                  İlk günden hangi işi net yapmalı?
                </div>
                <div className="grid grid-cols-1 gap-2">
                  {catalog.assistantRoles.map((item) => {
                    const selected = assistantRole === item.key;
                    return (
                      <button
                        key={item.key}
                        type="button"
                        onClick={() => setAssistantRole(item.key)}
                        className={`w-full text-left rounded-xl border p-3 transition-all ${
                          selected
                            ? "border-orange-400 bg-orange-50/50 text-slate-900"
                            : "border-slate-200 bg-white text-slate-800 hover:bg-slate-50"
                        }`}
                      >
                        <div className="text-xs font-semibold">{item.title}</div>
                        <div className="text-[11px] text-slate-500 mt-0.5">
                          {item.description}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>

              {/* Ana Kanal */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="text-xs font-semibold text-slate-900">
                  Sorular en çok nereden geliyor?
                </div>
                <div className="grid grid-cols-2 gap-2">
                  {catalog.primaryChannels.map((item) => {
                    const selected = primaryChannel === item.key;
                    return (
                      <button
                        key={item.key}
                        type="button"
                        onClick={() => setPrimaryChannel(item.key)}
                        className={`text-left rounded-xl border p-2.5 transition-all ${
                          selected
                            ? "bg-slate-900 text-white border-slate-900"
                            : "bg-white text-slate-800 border-slate-200 hover:bg-slate-50"
                        }`}
                      >
                        <div className="text-xs font-semibold">{item.title}</div>
                        <div
                          className={`text-[10px] mt-0.5 line-clamp-1 ${
                            selected ? "text-slate-300" : "text-slate-500"
                          }`}
                        >
                          {item.description}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>

              {/* İlk Bilgi Kaynağı */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="text-xs font-semibold text-slate-900">
                  AI&apos;yı en kolay nasıl besleyeceksin?
                </div>
                <div className="grid grid-cols-1 gap-2">
                  {catalog.knowledgeMethods.map((item) => {
                    const selected = initialKnowledgeMethod === item.key;
                    return (
                      <button
                        key={item.key}
                        type="button"
                        onClick={() => setInitialKnowledgeMethod(item.key)}
                        className={`w-full text-left rounded-xl border p-3 transition-all ${
                          selected
                            ? "border-orange-400 bg-orange-50/50 text-slate-900"
                            : "border-slate-200 bg-white text-slate-800 hover:bg-slate-50"
                        }`}
                      >
                        <div className="text-xs font-semibold">{item.title}</div>
                        <div className="text-[11px] text-slate-500 mt-0.5">
                          {item.description}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>
            </div>
          )}

          {/* ADIM 2/4 — İçerik */}
          {step === 2 && (
            <div className="space-y-4">
              <div>
                <h3 className="text-sm font-bold text-slate-900">
                  Adım 2/4 — İçerik (Karşılama, AI seçimi ve çalışma planı)
                </h3>
                <p className="text-xs text-slate-500">
                  Karşılama mesajınız sağdaki canlı önizlemede anında güncellenir.
                </p>
              </div>

              <div className="rounded-2xl border border-slate-200 p-4 space-y-3">
                <div className="space-y-1.5">
                  <Label className="text-xs font-semibold">İş Özeti</Label>
                  <Textarea
                    rows={3}
                    value={businessSummary}
                    onChange={(e) => setBusinessSummary(e.target.value)}
                    placeholder="Şirketinizin sunduğu ürün/hizmetleri ve konuşma üslubunu kısaca özetleyin…"
                  />
                </div>

                <div className="space-y-1.5">
                  <Label className="text-xs font-semibold">
                    İlk İzlenim (Karşılama Mesajı)
                  </Label>
                  <Textarea
                    rows={2}
                    value={welcomeMessage}
                    onChange={(e) => setWelcomeMessage(e.target.value)}
                    placeholder="Merhaba, sağlıklı günler diliyorum."
                  />
                </div>

                <div className="space-y-1.5">
                  <Label className="text-xs font-semibold">Sık Sorular (Key FAQ)</Label>
                  <Textarea
                    rows={3}
                    value={keyFaq}
                    onChange={(e) => setKeyFaq(e.target.value)}
                    placeholder="Her satıra bir soru-cevap çifti ekleyebilirsiniz…"
                  />
                </div>
              </div>

              {/* Otomasyon */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2">
                <Label className="text-xs font-semibold">Otomasyon (AI Çalışma Zamanı)</Label>
                <Select value={aiScheduleMode} onValueChange={(val) => setAiScheduleMode(val)}>
                  <SelectTrigger>
                    <SelectValue placeholder="Seçiniz">
                      {SCHEDULE_LABELS[aiScheduleMode] ?? aiScheduleMode}
                    </SelectValue>
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="ALWAYS_AI">Her zaman AI</SelectItem>
                    <SelectItem value="OFF_HOURS_AI">Mesai saatleri dışında</SelectItem>
                    <SelectItem value="HUMAN_ONLY">Sadece insan</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              {/* Bildirim */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-3">
                <div className="flex items-center justify-between">
                  <Label className="text-xs font-semibold">Bildirim (Mesaj Zil Sesi)</Label>
                  <Switch
                    checked={soundNotificationEnabled}
                    onCheckedChange={(checked) => setSoundNotificationEnabled(checked)}
                  />
                </div>
                <div className="flex items-center gap-2">
                  <div className="flex-1">
                    <Select value={soundTone} onValueChange={(val) => setSoundTone(val)}>
                      <SelectTrigger>
                        <SelectValue placeholder="Zil Tonu">
                          {TONE_LABELS[soundTone] ?? soundTone}
                        </SelectValue>
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="CLASSIC">Klasik</SelectItem>
                        <SelectItem value="MODERN">Modern</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={() => playTestTone(soundTone)}
                    className="h-10 shrink-0"
                  >
                    <Volume2 className="h-4 w-4" />
                    <span>Sesi Test Et</span>
                  </Button>
                </div>
              </div>

              {/* Güvenlik */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-1.5">
                <Label className="text-xs font-semibold">
                  Güvenlik (İzinli Domain Listesi)
                </Label>
                <Textarea
                  rows={2}
                  value={allowedDomains}
                  onChange={(e) => setAllowedDomains(e.target.value)}
                  placeholder="ornek.com, domain.com"
                />
                <p className="text-[11px] text-slate-500">
                  Boş bırakılırsa tüm domainlerden gelen widget isteklerine izin verilir.
                </p>
              </div>
            </div>
          )}

          {/* ADIM 3/4 — Önizleme */}
          {step === 3 && (
            <div className="space-y-4">
              <div>
                <h3 className="text-sm font-bold text-slate-900">
                  Adım 3/4 — Önizleme (Widget simülasyonu)
                </h3>
                <p className="text-xs text-slate-500">
                  Yayına çıkmadan önce hızlı hazırlık kontrolü.
                </p>
              </div>

              {/* Hazırlık Skoru Kartı */}
              <div className="rounded-2xl border border-teal-200 bg-teal-50/60 p-4 flex items-center justify-between">
                <div className="space-y-1">
                  <Badge className="bg-teal-600 text-white text-[10px]">YAYINA YAKIN</Badge>
                  <div className="text-xs font-semibold text-slate-900">
                    Yayına çıkmadan önce hızlı hazırlık kontrolü
                  </div>
                  <div className="text-[11px] text-slate-600">
                    Sağdaki canlı simülasyondan botunuzu hemen test edebilirsiniz.
                  </div>
                </div>
                <div className="flex h-14 w-14 shrink-0 flex-col items-center justify-center rounded-2xl bg-teal-600 text-white font-bold">
                  <span className="text-lg leading-none">%{readiness.profileScore}</span>
                  <span className="text-[9px] font-normal uppercase mt-0.5">Skor</span>
                </div>
              </div>

              {/* Güçlü Alanlar */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2">
                <div className="text-xs font-semibold text-emerald-700">Güçlü alanlar</div>
                {readiness.strengths.length === 0 ? (
                  <p className="text-xs text-slate-500">Henüz tamamlanan madde yok.</p>
                ) : (
                  <div className="space-y-1.5">
                    {readiness.strengths.map((item) => (
                      <div
                        key={item}
                        className="flex items-start gap-2 rounded-xl border border-emerald-200 bg-emerald-50/70 p-2.5 text-xs text-emerald-900"
                      >
                        <CheckCircle2 className="h-4 w-4 text-emerald-600 shrink-0 mt-0.5" />
                        <span>{item}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              {/* Eksik Kalan Noktalar */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2">
                <div className="text-xs font-semibold text-amber-700">Eksik kalan noktalar</div>
                {readiness.missingPoints.length === 0 ? (
                  <p className="text-xs text-emerald-700 font-medium">
                    Tüm temel kurulum adımları tamamlandı!
                  </p>
                ) : (
                  <div className="space-y-1.5">
                    {readiness.missingPoints.map((item) => (
                      <div
                        key={item}
                        className="flex items-start gap-2 rounded-xl border border-amber-200 bg-amber-50/70 p-2.5 text-xs text-amber-900"
                      >
                        <AlertCircle className="h-4 w-4 text-amber-600 shrink-0 mt-0.5" />
                        <span>{item}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              {/* Önerilen sonraki adım & Önemli not */}
              <div className="grid grid-cols-1 gap-3">
                <div className="rounded-2xl border border-violet-200 bg-violet-50/50 p-3.5 space-y-1">
                  <div className="text-xs font-semibold text-violet-900">
                    Önerilen sonraki adım
                  </div>
                  <p className="text-xs text-violet-700">
                    Sağ taraftaki canlı Playground üzerinden müşterilerinizin en sık sorduğu 2-3 soruyu yazarak yanıt kalitesini doğrulayın.
                  </p>
                </div>
                <div className="rounded-2xl border border-slate-200 bg-slate-50 p-3.5 space-y-1">
                  <div className="text-xs font-semibold text-slate-800">Önemli not</div>
                  <p className="text-xs text-slate-600">
                    Playground test mesajları aylık ana mesaj kotanızdan düşmez; 200 adet bağımsız test krediniz kullanılır.
                  </p>
                </div>
              </div>
            </div>
          )}

          {/* ADIM 4/4 — Yayınla */}
          {step === 4 && (
            <div className="space-y-4">
              <div className="rounded-2xl border border-emerald-200 bg-emerald-50/60 p-4 space-y-2">
                <div className="flex items-center gap-2 text-emerald-800 font-bold text-sm">
                  <Sparkles className="h-4 w-4 text-emerald-600" />
                  <span>Mükemmel! Widget Hazır.</span>
                </div>
                <p className="text-xs text-emerald-700">
                  Aşağıdaki embed kodunu sitenizin <code>&lt;/body&gt;</code> etiketinden hemen önce yapıştırarak asistanınızı dakikalar içinde yayına alabilirsiniz.
                </p>
              </div>

              {/* 3 Sayaç */}
              <div className="grid grid-cols-3 gap-2">
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-center">
                  <div className="text-[10px] font-semibold uppercase text-slate-500">
                    PROFİL SKORU
                  </div>
                  <div className="text-base font-bold text-slate-900 mt-0.5">
                    %{readiness.profileScore}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-center">
                  <div className="text-[10px] font-semibold uppercase text-slate-500">
                    FAQ SEED
                  </div>
                  <div className="text-base font-bold text-slate-900 mt-0.5">
                    {readiness.faqSeedCount}
                  </div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-center">
                  <div className="text-[10px] font-semibold uppercase text-slate-500">
                    KAYNAK
                  </div>
                  <div className="text-base font-bold text-slate-900 mt-0.5">
                    {readiness.sourceCount}
                  </div>
                </div>
              </div>

              {/* Launch Checklist */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2">
                <div className="text-xs font-semibold text-slate-900">Launch Checklist</div>
                {[
                  { label: "Asistan profili", ok: readiness.profileScore >= 50 },
                  { label: "Başlangıç soru-cevap bilgileri", ok: readiness.faqSeedCount > 0 || businessSummary.trim().length > 0 },
                  { label: "Bilgi kaynakları", ok: readiness.sourceCount > 0 || isTrained },
                  { label: "Yayın kanalı", ok: Boolean(primaryChannel) },
                ].map((item) => (
                  <div
                    key={item.label}
                    className="flex items-center justify-between text-xs py-1 border-b border-slate-100 last:border-0"
                  >
                    <span className="text-slate-700">{item.label}</span>
                    <Badge
                      variant={item.ok ? "default" : "outline"}
                      className={
                        item.ok
                          ? "bg-emerald-50 text-emerald-700 border-emerald-200 text-[10px]"
                          : "text-[10px]"
                      }
                    >
                      {item.ok ? "Hazır" : "Opsiyonel"}
                    </Badge>
                  </div>
                ))}
              </div>

              {/* WEB SİTENİZE EKLEYİN (EMBED.JS) */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-bold uppercase tracking-wider text-slate-700">
                    WEB SİTENİZE EKLEYİN (EMBED.JS)
                  </span>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => copyText(embedCode, "embed")}
                    className="h-7 text-xs"
                  >
                    {copiedField === "embed" ? (
                      <>
                        <Check className="h-3.5 w-3.5 text-emerald-600" />
                        <span>KOPYALANDI</span>
                      </>
                    ) : (
                      <>
                        <Copy className="h-3.5 w-3.5" />
                        <span>KOPYALA</span>
                      </>
                    )}
                  </Button>
                </div>
                <pre className="rounded-xl bg-slate-950 text-slate-100 p-3.5 text-[11px] font-mono overflow-x-auto leading-relaxed">
                  {embedCode}
                </pre>
              </div>

              {/* DİREKT PAYLAŞIM LİNKİ */}
              <div className="rounded-2xl border border-slate-200 p-4 space-y-2.5">
                <div className="flex items-center justify-between">
                  <span className="text-xs font-bold uppercase tracking-wider text-slate-700">
                    DİREKT PAYLAŞIM LİNKİ
                  </span>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    onClick={() => copyText(directShareUrl, "link")}
                    className="h-7 text-xs"
                  >
                    {copiedField === "link" ? (
                      <>
                        <Check className="h-3.5 w-3.5 text-emerald-600" />
                        <span>KOPYALANDI</span>
                      </>
                    ) : (
                      <>
                        <Copy className="h-3.5 w-3.5" />
                        <span>KOPYALA</span>
                      </>
                    )}
                  </Button>
                </div>
                <Input value={directShareUrl} readOnly className="text-xs font-mono bg-slate-50" />
              </div>
            </div>
          )}
        </div>

        {/* 3. Alt Sabit Bar */}
        <div className="p-4 border-t border-slate-200 bg-white flex items-center justify-between shrink-0">
          <div>
            {step > 1 && (
              <Button
                type="button"
                variant="outline"
                onClick={() => setStep((prev) => (prev > 1 ? ((prev - 1) as 1 | 2 | 3 | 4) : prev))}
              >
                <ArrowLeft className="h-4 w-4" />
                <span>Geri</span>
              </Button>
            )}
          </div>
          <div>
            {step < 4 ? (
              <Button
                type="button"
                disabled={saving}
                onClick={() => void saveSettings(true)}
                className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
              >
                <span>{saving ? "Kaydediliyor…" : "Devam Et"}</span>
                <ArrowRight className="h-4 w-4" />
              </Button>
            ) : (
              <Button
                type="button"
                disabled={saving}
                onClick={() => void saveSettings(false)}
                className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
              >
                <span>{saving ? "Kaydediliyor…" : "Kaydet"}</span>
              </Button>
            )}
          </div>
        </div>
      </div>

      {/* SAĞ SÜTUN: CANLI BOT GÖRÜNÜMÜ TUVALİ */}
      <div className="bg-slate-50/70 flex flex-col items-center justify-center p-6 relative overflow-y-auto">
        <div className="w-[370px] h-[560px] bg-white rounded-3xl shadow-2xl border border-slate-200 flex flex-col overflow-hidden">
          {/* Üst Bar */}
          <div
            style={{ backgroundColor: primaryColor || "#7c3aed" }}
            className="px-4 py-3.5 text-white flex items-center justify-between shrink-0 transition-colors"
          >
            <div className="flex items-center gap-3 min-w-0">
              <div className="h-10 w-10 rounded-full bg-white/20 flex items-center justify-center overflow-hidden shrink-0 border border-white/25">
                {logoUrl ? (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={logoUrl}
                    alt={brandName || widgetTitle}
                    className="h-full w-full object-cover"
                  />
                ) : (
                  <Briefcase className="h-5 w-5 text-white" />
                )}
              </div>
              <div className="min-w-0">
                <div className="font-semibold text-sm leading-tight truncate">
                  {widgetTitle || "Canlı Destek"}
                </div>
                <div className="text-[11px] text-white/85 truncate mt-0.5">
                  {widgetSubtitle || "Genel destek • Online"}
                </div>
              </div>
            </div>
            <button
              type="button"
              onClick={() => setChatHistory([])}
              className="p-2 rounded-full hover:bg-white/15 text-white/90 transition-colors"
              title="Sohbeti Sıfırla"
              aria-label="Sohbeti Sıfırla"
            >
              <RotateCcw className="h-4 w-4" />
            </button>
          </div>

          {/* Mesaj Alanı */}
          <div
            ref={chatScrollRef}
            className="flex-1 overflow-y-auto p-4 space-y-3 bg-slate-50/60"
          >
            {/* İlk Karşılama Balonu */}
            <div className="flex items-start gap-2">
              <div
                style={{ backgroundColor: primaryColor || "#7c3aed" }}
                className="h-7 w-7 rounded-full flex items-center justify-center text-white text-xs font-bold shrink-0 mt-0.5"
              >
                {(brandName || widgetTitle || "D")[0]?.toUpperCase()}
              </div>
              <div className="max-w-[80%] rounded-2xl rounded-tl-sm bg-white border border-slate-200/80 px-3.5 py-2.5 text-xs text-slate-800 shadow-2xs leading-relaxed">
                {welcomeMessage || "Merhaba, sağlıklı günler diliyorum."}
              </div>
            </div>

            {chatHistory.map((msg, idx) =>
              msg.role === "user" ? (
                <div key={idx} className="flex justify-end">
                  <div
                    style={{ backgroundColor: primaryColor || "#7c3aed" }}
                    className="max-w-[80%] rounded-2xl rounded-tr-sm px-3.5 py-2.5 text-xs text-white shadow-2xs leading-relaxed"
                  >
                    {msg.content}
                  </div>
                </div>
              ) : (
                <div key={idx} className="flex items-start gap-2">
                  <div
                    style={{ backgroundColor: primaryColor || "#7c3aed" }}
                    className="h-7 w-7 rounded-full flex items-center justify-center text-white text-xs font-bold shrink-0 mt-0.5"
                  >
                    {(brandName || widgetTitle || "D")[0]?.toUpperCase()}
                  </div>
                  <div className="max-w-[80%] rounded-2xl rounded-tl-sm bg-white border border-slate-200/80 px-3.5 py-2.5 text-xs text-slate-800 shadow-2xs leading-relaxed whitespace-pre-wrap">
                    {msg.content}
                  </div>
                </div>
              )
            )}

            {chatBusy && (
              <div className="flex items-center gap-2 text-xs text-slate-400 pl-9">
                <span>Asistan yazıyor…</span>
              </div>
            )}
          </div>

          {/* Alt Input Alanı */}
          <div className="border-t border-slate-200 bg-white p-3 space-y-2 shrink-0">
            <form
              className="flex items-center gap-2"
              onSubmit={(e) => void handleSendPlayground(e)}
            >
              <Input
                value={chatInput}
                onChange={(e) => setChatInput(e.target.value)}
                placeholder="Mesaj..."
                disabled={chatBusy}
                className="h-9 text-xs rounded-full bg-slate-50"
              />
              <button
                type="submit"
                disabled={chatBusy || !chatInput.trim()}
                style={{ backgroundColor: primaryColor || "#7c3aed" }}
                className="h-9 w-9 rounded-full flex items-center justify-center text-white shrink-0 disabled:opacity-50 transition-opacity"
                aria-label="Mesaj Gönder"
              >
                <Send className="h-4 w-4" />
              </button>
            </form>

            {(canRemoveBadge ? showBadge : true) && (
              <div className="text-center text-[10px] text-slate-400 font-medium">
                Powered by <span className="text-slate-600 font-semibold">Daribase</span>
              </div>
            )}
          </div>
        </div>

        {/* Widget'ın hemen altında küçük rozet */}
        <div className="mt-4">
          <Badge variant="outline" className="bg-white text-slate-700 border-slate-200 px-3 py-1 text-xs shadow-2xs">
            Playground Test Kredisi: {usedPlayground} / {playgroundCap}
          </Badge>
        </div>
      </div>
    </div>
  );
}
