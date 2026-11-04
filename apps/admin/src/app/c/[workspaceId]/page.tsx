"use client";

import React, {
  Suspense,
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from "react";
import { useParams, useSearchParams } from "next/navigation";
import type {
  PublicWidgetConfig,
  PublicWidgetMessage,
  PublicWidgetSendMessageResponse,
  PublicWidgetSessionResponse,
} from "@/lib/types";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import {
  Briefcase,
  Loader2,
  MessageCircle,
  Mic,
  Paperclip,
  RotateCcw,
  Send,
  ShieldAlert,
  X,
} from "lucide-react";

const DEFAULT_SUGGESTED_QUESTIONS = [
  "Neler yapabiliyorsunuz?",
  "Fiyatlandırma nasıl işliyor?",
  "Nasıl başlayabilirim?",
];

function PublicLiveBotContent() {
  const params = useParams<{ workspaceId: string }>();
  const searchParams = useSearchParams();

  const workspaceId = Array.isArray(params?.workspaceId)
    ? params.workspaceId[0]
    : params?.workspaceId ?? "";
  const shareKey = searchParams.get("key") ?? "";
  const isEmbedMode = searchParams.get("embed") === "1";

  const storageKey = `daribase_guest_${workspaceId}`;

  const [config, setConfig] = useState<PublicWidgetConfig | null>(null);
  const [sessionToken, setSessionToken] = useState<string>("");
  const [messages, setMessages] = useState<PublicWidgetMessage[]>([]);
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(true);
  const [resetting, setResetting] = useState(false);
  const [sending, setSending] = useState(false);
  const [inactiveError, setInactiveError] = useState<string | null>(null);
  const [sendError, setSendError] = useState<string | null>(null);
  const [privacyDismissed, setPrivacyDismissed] = useState(false);
  const [minimized, setMinimized] = useState(false);
  const [viewportHeight, setViewportHeight] = useState<number | null>(null);

  useEffect(() => {
    const updateViewportHeight = () => setViewportHeight(window.visualViewport?.height ?? window.innerHeight);
    updateViewportHeight();
    window.visualViewport?.addEventListener("resize", updateViewportHeight);
    window.addEventListener("resize", updateViewportHeight);
    return () => {
      window.visualViewport?.removeEventListener("resize", updateViewportHeight);
      window.removeEventListener("resize", updateViewportHeight);
    };
  }, []);

  const chatHeight = viewportHeight === null ? undefined : Math.min(640, Math.max(0, viewportHeight - 32));
  const messagesEndRef = useRef<HTMLDivElement | null>(null);

  const scrollToBottom = useCallback(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, []);

  useEffect(() => {
    scrollToBottom();
  }, [messages, sending, scrollToBottom]);

  const initSession = useCallback(
    async (reset = false, currentKey = shareKey) => {
      if (!workspaceId) return;
      if (reset) {
        setResetting(true);
      }

      try {
        const storedToken =
          !reset && typeof window !== "undefined"
            ? window.localStorage.getItem(storageKey) ?? ""
            : "";

        const headers: Record<string, string> = {
          "Content-Type": "application/json",
        };
        if (storedToken) {
          headers.Authorization = `Bearer ${storedToken}`;
        }

        const res = await fetch(`/public/widget/${workspaceId}/session`, {
          method: "POST",
          headers,
          body: JSON.stringify({
            key: currentKey || undefined,
            reset,
            sessionToken: storedToken || undefined,
          }),
        });

        if (res.status === 403 || res.status === 404) {
          setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
          return;
        }

        if (!res.ok) {
          throw new Error("Sohbet oturumu başlatılamadı.");
        }

        const sessionData = (await res.json()) as PublicWidgetSessionResponse;
        setSessionToken(sessionData.sessionToken);
        setMessages(sessionData.messages ?? []);

        if (typeof window !== "undefined" && sessionData.sessionToken) {
          window.localStorage.setItem(storageKey, sessionData.sessionToken);
        }
      } catch {
        setSendError("Oturum başlatılırken bir bağlantı sorunu oluştu.");
      } finally {
        setResetting(false);
      }
    },
    [workspaceId, shareKey, storageKey]
  );

  useEffect(() => {
    if (!workspaceId) return;

    let cancelled = false;

    const bootstrap = async () => {
      setLoading(true);
      setInactiveError(null);
      setSendError(null);

      // Direct share page requires ?key=... unless opened in iframe embed mode (?embed=1)
      if (!shareKey && !isEmbedMode) {
        setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
        setLoading(false);
        return;
      }

      try {
        const query = shareKey
          ? `?key=${encodeURIComponent(shareKey)}`
          : "";
        const configRes = await fetch(
          `/public/widget/${workspaceId}/config${query}`,
          {
            method: "GET",
            cache: "no-store",
          }
        );

        if (cancelled) return;

        if (configRes.status === 403 || configRes.status === 404) {
          setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
          setLoading(false);
          return;
        }

        if (!configRes.ok) {
          setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
          setLoading(false);
          return;
        }

        const cfg = (await configRes.json()) as PublicWidgetConfig;
        if (cancelled) return;

        setConfig(cfg);
        await initSession(false, shareKey);
      } catch {
        if (!cancelled) {
          setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void bootstrap();

    return () => {
      cancelled = true;
    };
  }, [workspaceId, shareKey, isEmbedMode, initSession]);

  const handleSendMessage = async (textToSend?: string) => {
    const content = (textToSend ?? input).trim();
    if (!content || sending || !workspaceId) return;

    if (!textToSend) {
      setInput("");
    }
    setSendError(null);

    const optimisticId =
      typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
        ? crypto.randomUUID()
        : `temp-${Date.now()}`;

    const optimisticGuestMsg: PublicWidgetMessage = {
      id: optimisticId,
      sequence: messages.length + 1,
      senderKind: "GUEST",
      content,
      createdAt: new Date().toISOString(),
    };

    setMessages((prev) => [...prev, optimisticGuestMsg]);
    setSending(true);

    try {
      let activeToken = sessionToken;
      if (!activeToken && typeof window !== "undefined") {
        activeToken = window.localStorage.getItem(storageKey) ?? "";
      }

      const sendRequest = async (tokenToUse: string) =>
        fetch(`/public/widget/${workspaceId}/messages`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${tokenToUse}`,
          },
          body: JSON.stringify({
            content,
            clientMessageId: optimisticId,
            key: shareKey || undefined,
            sessionToken: tokenToUse,
          }),
        });

      let res = await sendRequest(activeToken);

      // If token expired or was invalidated, create a fresh session once and retry
      if (res.status === 401) {
        const freshRes = await fetch(`/public/widget/${workspaceId}/session`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            key: shareKey || undefined,
            reset: true,
          }),
        });
        if (freshRes.ok) {
          const freshSession =
            (await freshRes.json()) as PublicWidgetSessionResponse;
          activeToken = freshSession.sessionToken;
          setSessionToken(activeToken);
          if (typeof window !== "undefined") {
            window.localStorage.setItem(storageKey, activeToken);
          }
          res = await sendRequest(activeToken);
        }
      }

      if (res.status === 403) {
        setInactiveError("Bu sohbet bağlantısı şu anda aktif değil.");
        return;
      }

      if (!res.ok) {
        throw new Error("Mesaj gönderilemedi.");
      }

      const data = (await res.json()) as PublicWidgetSendMessageResponse;

      setMessages((prev) => {
        const filtered = prev.filter((m) => m.id !== optimisticId);
        const updated = [...filtered, data.guestMessage];
        if (data.aiMessage) {
          updated.push(data.aiMessage);
        }
        return updated;
      });
    } catch {
      setSendError("Mesaj gönderilemedi. Lütfen tekrar deneyin.");
    } finally {
      setSending(false);
    }
  };

  const handleFormSubmit = (e: FormEvent) => {
    e.preventDefault();
    void handleSendMessage();
  };

  const handleResetConversation = async () => {
    if (resetting || sending) return;
    setSendError(null);
    await initSession(true, shareKey);
  };

  const primaryColor = config?.primaryColor || "#7c3aed";
  const suggestedQuestions =
    config?.suggestedQuestions && config.suggestedQuestions.length > 0
      ? config.suggestedQuestions
      : DEFAULT_SUGGESTED_QUESTIONS;

  return (
    <div className="min-h-screen w-full bg-[#fafafa] bg-[linear-gradient(to_right,#e5e7eb_1px,transparent_1px),linear-gradient(to_bottom,#e5e7eb_1px,transparent_1px)] bg-[size:36px_36px] flex items-center justify-center md:justify-end md:items-end md:p-10 p-4">
      {loading ? (
        <div className="w-full max-w-[400px] h-[640px] bg-white rounded-[24px] shadow-[0_20px_60px_-15px_rgba(0,0,0,0.18)] border border-slate-200/80 flex flex-col items-center justify-center p-8 text-center">
          <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed] mb-3" />
          <p className="text-sm font-medium text-slate-600">
            Canlı destek bağlanıyor...
          </p>
        </div>
      ) : inactiveError || !config ? (
        <Card className="w-full max-w-[400px] rounded-[24px] shadow-[0_20px_60px_-15px_rgba(0,0,0,0.18)] border-slate-200/80 bg-white">
          <CardContent className="p-8 flex flex-col items-center text-center space-y-4">
            <div className="w-12 h-12 rounded-2xl bg-rose-50 text-rose-600 flex items-center justify-center">
              <ShieldAlert className="w-6 h-6" />
            </div>
            <div className="space-y-1.5">
              <h2 className="text-base font-bold text-slate-900">
                Bağlantı Kullanılamıyor
              </h2>
              <p className="text-sm text-slate-600 leading-relaxed">
                {inactiveError || "Bu sohbet bağlantısı şu anda aktif değil."}
              </p>
            </div>
          </CardContent>
        </Card>
      ) : minimized ? (
        <button
          type="button"
          onClick={() => setMinimized(false)}
          style={{ backgroundColor: primaryColor }}
          className="w-14 h-14 rounded-full shadow-[0_12px_32px_-8px_rgba(0,0,0,0.3)] flex items-center justify-center text-white transition-transform hover:scale-105 cursor-pointer"
          aria-label="Sohbeti Aç"
        >
          <MessageCircle className="w-6 h-6" />
        </button>
      ) : (
        /* 2. Sohbet Penceresi */
        <div className="w-full max-w-[400px] h-[640px] bg-white rounded-[24px] shadow-[0_20px_60px_-15px_rgba(0,0,0,0.18)] border border-slate-200/80 flex flex-col overflow-hidden">
          {/* A) Üst Başlık Barı */}
          <div
            className="px-5 py-4 text-white flex items-center justify-between shrink-0"
            style={{ backgroundColor: primaryColor }}
          >
            <div className="flex items-center gap-3 min-w-0">
              <div className="w-10 h-10 rounded-full bg-white/20 flex items-center justify-center overflow-hidden shrink-0">
                {config.logoUrl ? (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={config.logoUrl}
                    alt={config.widgetTitle || "Logo"}
                    className="w-full h-full object-cover"
                  />
                ) : (
                  <Briefcase className="w-5 h-5 text-white" />
                )}
              </div>

              <div className="min-w-0">
                <h1 className="font-bold text-sm leading-tight truncate">
                  {config.widgetTitle || "Canlı Destek"}
                </h1>
                <div className="flex items-center gap-1.5 mt-0.5">
                  <span className="w-2 h-2 rounded-full bg-emerald-400 inline-block" />
                  <span className="text-xs text-white/90 truncate">
                    {config.widgetSubtitle || "Çevrimiçi"}
                  </span>
                </div>
              </div>
            </div>

            <div className="flex items-center gap-1 shrink-0">
              <button
                type="button"
                onClick={handleResetConversation}
                disabled={resetting || sending}
                title="Sohbeti Sıfırla"
                className="w-8 h-8 rounded-full hover:bg-white/15 flex items-center justify-center text-white/90 hover:text-white transition-colors cursor-pointer disabled:opacity-50"
              >
                <RotateCcw
                  className={`w-4 h-4 ${resetting ? "animate-spin" : ""}`}
                />
              </button>
              <button
                type="button"
                onClick={() => setMinimized(true)}
                title="Küçült"
                className="w-8 h-8 rounded-full hover:bg-white/15 flex items-center justify-center text-white/90 hover:text-white transition-colors cursor-pointer"
              >
                <X className="w-4 h-4" />
              </button>
            </div>
          </div>

          {/* B) AI Şeffaflık Şeridi */}
          <div className="px-4 py-2 bg-slate-50 border-b border-slate-100 text-[11px] text-slate-500 text-center shrink-0">
            {config.aiNotice ||
              "AI asistanımızla konuşuyorsunuz. Gerekirse bir temsilci devralabilir."}
          </div>

          {/* C) Orta Mesajlaşma ve Hızlı Sorular Alanı */}
          <div className="flex-1 overflow-y-auto p-4 space-y-4 bg-white">
            {/* İlk Karşılama Balonu */}
            <div className="flex justify-start">
              <div className="bg-white border border-slate-200/90 shadow-sm rounded-2xl rounded-tl-sm px-4 py-3 text-sm text-slate-800 max-w-[85%] leading-relaxed">
                {config.welcomeMessage || "Merhaba, sağlıklı günler diliyorum."}
              </div>
            </div>

            {/* "ŞUNLARI SORABİLİRSİNİZ" (Suggested Questions Chips) */}
            {messages.length === 0 && suggestedQuestions.length > 0 && (
              <div className="space-y-2 pt-1">
                <p className="text-[11px] font-semibold tracking-wider text-slate-400 uppercase">
                  ŞUNLARI SORABİLİRSİNİZ
                </p>
                <div className="flex flex-wrap gap-2">
                  {suggestedQuestions.map((question, idx) => (
                    <button
                      key={`${idx}-${question}`}
                      type="button"
                      disabled={sending}
                      onClick={() => void handleSendMessage(question)}
                      className="text-left text-xs font-medium px-3.5 py-2 rounded-full bg-white border border-slate-200 text-slate-700 hover:border-slate-300 hover:bg-slate-50 transition-all shadow-2xs cursor-pointer disabled:opacity-50"
                    >
                      {question}
                    </button>
                  ))}
                </div>
              </div>
            )}

            {/* Mesaj Listesi */}
            {messages.map((msg) => {
              const isGuest = msg.senderKind === "GUEST";
              return (
                <div
                  key={msg.id}
                  className={`flex ${isGuest ? "justify-end" : "justify-start"}`}
                >
                  <div
                    style={
                      isGuest ? { backgroundColor: primaryColor } : undefined
                    }
                    className={
                      isGuest
                        ? "text-white rounded-2xl rounded-tr-sm px-4 py-2.5 text-sm max-w-[85%] shadow-xs leading-relaxed whitespace-pre-wrap break-words"
                        : "bg-slate-50 border border-slate-200 text-slate-800 rounded-2xl rounded-tl-sm px-4 py-2.5 text-sm max-w-[85%] leading-relaxed whitespace-pre-wrap break-words"
                    }
                  >
                    {msg.content}
                  </div>
                </div>
              );
            })}

            {/* AI Yanıt Üretirken "Yazıyor..." Balonu */}
            {sending && (
              <div className="flex justify-start">
                <div className="bg-slate-50 border border-slate-200 text-slate-600 rounded-2xl rounded-tl-sm px-4 py-3 text-xs flex items-center gap-2">
                  <span className="flex items-center gap-1">
                    <span className="w-1.5 h-1.5 rounded-full bg-slate-400 animate-bounce [animation-delay:-0.3s]" />
                    <span className="w-1.5 h-1.5 rounded-full bg-slate-400 animate-bounce [animation-delay:-0.15s]" />
                    <span className="w-1.5 h-1.5 rounded-full bg-slate-400 animate-bounce" />
                  </span>
                  <span className="text-[11px] text-slate-500 font-medium">
                    Yazıyor...
                  </span>
                </div>
              </div>
            )}

            {sendError && (
              <div className="text-center">
                <span className="inline-block text-[11px] text-rose-600 bg-rose-50 border border-rose-200 rounded-lg px-3 py-1">
                  {sendError}
                </span>
              </div>
            )}

            <div ref={messagesEndRef} />
          </div>

          {/* D) Gizlilik Politikası (KVKK) Kutusu (Kapatılabilir) */}
          {!privacyDismissed && (
            <div className="mx-3 mb-2 p-3 rounded-xl bg-slate-50 border border-slate-200/80 flex items-start justify-between gap-2 text-[11px] text-slate-600 shrink-0">
              <p className="leading-snug">
                Burada sohbet ederek, paylaştığınız verilerin işlenmesini{" "}
                <span className="underline font-medium cursor-pointer text-slate-800">
                  Gizlilik Politikası
                </span>{" "}
                kapsamında kabul etmiş olursunuz.
              </p>
              <button
                type="button"
                onClick={() => setPrivacyDismissed(true)}
                aria-label="Gizlilik bildirimini kapat"
                className="text-slate-400 hover:text-slate-600 p-0.5 shrink-0 cursor-pointer"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          )}

          {/* E) Alt Zengin Mesaj Input Barı */}
          <div className="p-3 pt-1 bg-white shrink-0">
            <form
              onSubmit={handleFormSubmit}
              className="flex items-center gap-2 px-3 py-2 rounded-2xl border border-slate-200 bg-slate-50/50 focus-within:bg-white focus-within:border-slate-300 transition-colors"
            >
              <button
                type="button"
                tabIndex={-1}
                aria-label="Dosya ekle"
                className="text-slate-400 hover:text-slate-600 transition-colors p-1 cursor-pointer"
              >
                <Paperclip className="w-4 h-4" />
              </button>

              <input
                type="text"
                value={input}
                onChange={(e) => setInput(e.target.value)}
                placeholder="Mesaj yazın..."
                maxLength={4000}
                disabled={sending}
                className="flex-1 bg-transparent text-sm text-slate-900 placeholder:text-slate-400 outline-none"
              />

              <button
                type="button"
                tabIndex={-1}
                aria-label="Sesli mesaj"
                className="text-slate-400 hover:text-slate-600 transition-colors p-1 cursor-pointer"
              >
                <Mic className="w-4 h-4" />
              </button>

              <Button
                type="submit"
                size="icon"
                disabled={!input.trim() || sending}
                style={{
                  backgroundColor: input.trim() ? primaryColor : undefined,
                }}
                className="w-8 h-8 rounded-xl text-white shrink-0 disabled:opacity-40"
              >
                <Send className="w-3.5 h-3.5" />
              </Button>
            </form>

            {config.showBadge && (
              <div className="text-[11px] text-slate-400 text-center mt-2 font-medium">
                Powered by <span className="font-semibold text-slate-500">Daribase</span>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

export default function PublicLiveBotPage() {
  return (
    <Suspense
      fallback={
        <div className="min-h-screen w-full bg-[#fafafa] bg-[linear-gradient(to_right,#e5e7eb_1px,transparent_1px),linear-gradient(to_bottom,#e5e7eb_1px,transparent_1px)] bg-[size:36px_36px] flex items-center justify-center md:justify-end md:items-end md:p-10 p-4">
          <div className="w-full max-w-[400px] h-[640px] bg-white rounded-[24px] shadow-[0_20px_60px_-15px_rgba(0,0,0,0.18)] border border-slate-200/80 flex flex-col items-center justify-center p-8 text-center">
            <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed] mb-3" />
            <p className="text-sm font-medium text-slate-600">
              Canlı destek yükleniyor...
            </p>
          </div>
        </div>
      }
    >
      <PublicLiveBotContent />
    </Suspense>
  );
}
