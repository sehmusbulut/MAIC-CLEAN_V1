"use client";

import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from "react";
import { api, ApiError } from "@/lib/api";
import type {
  InboxContactSummary,
  InboxConversationDetail,
  InboxConversationItem,
  InboxCounts,
  InboxGroupBy,
  InboxListResponse,
  InboxMessageItem,
  InboxSortBy,
  InboxTab,
  PanelProps,
} from "@/lib/types";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Separator } from "@/components/ui/separator";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  MessageSquare,
  Search,
  Bot,
  UserCheck,
  AlertTriangle,
  CheckCircle2,
  Archive,
  ArchiveRestore,
  Send,
  Phone,
  Mail,
  Tag,
  FileText,
  Clock,
  ArrowUpDown,
  Layers,
  Sparkles,
  Volume2,
  RefreshCw,
  User,
  Loader2,
} from "lucide-react";

const DEFAULT_COUNTS: InboxCounts = {
  all: 0,
  pendingHandoff: 0,
  botActive: 0,
  assignedToMe: 0,
  resolved: 0,
  archived: 0,
  totalUnread: 0,
};

const SORT_LABELS: Record<InboxSortBy, string> = {
  priority: "Öncelik (Acil Üstte)",
  newest: "En Yeni",
  oldest: "En Eski",
};

const GROUP_LABELS: Record<InboxGroupBy, string> = {
  none: "Gruplama Yok",
  status: "Duruma Göre",
  channel: "Kanala Göre",
  date: "Tarihe Göre",
};

function formatShortTime(iso: string | null | undefined): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const now = new Date();
  const isToday =
    date.getDate() === now.getDate() &&
    date.getMonth() === now.getMonth() &&
    date.getFullYear() === now.getFullYear();

  if (isToday) {
    return date.toLocaleTimeString("tr-TR", {
      hour: "2-digit",
      minute: "2-digit",
    });
  }
  return date.toLocaleDateString("tr-TR", {
    day: "2-digit",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatFullDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString("tr-TR", {
    day: "2-digit",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function getInitials(name: string | null | undefined): string {
  const clean = (name ?? "Ziyaretçi").trim();
  if (!clean) return "Z";
  const parts = clean.split(/\s+/).filter(Boolean);
  if (parts.length === 1) {
    return parts[0].slice(0, 2).toUpperCase();
  }
  return `${parts[0][0] ?? ""}${parts[parts.length - 1][0] ?? ""}`.toUpperCase();
}

function playNotificationChime() {
  if (typeof window === "undefined") return;
  try {
    const AudioCtx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext?: typeof AudioContext })
        .webkitAudioContext;
    if (!AudioCtx) return;

    const ctx = new AudioCtx();
    const now = ctx.currentTime;

    const osc1 = ctx.createOscillator();
    const osc2 = ctx.createOscillator();
    const gain = ctx.createGain();

    osc1.type = "sine";
    osc2.type = "triangle";

    // Two-note pleasant chime (A5 -> E6)
    osc1.frequency.setValueAtTime(880, now);
    osc1.frequency.setValueAtTime(1318.5, now + 0.11);

    osc2.frequency.setValueAtTime(440, now);
    osc2.frequency.setValueAtTime(659.25, now + 0.11);

    gain.gain.setValueAtTime(0.001, now);
    gain.gain.exponentialRampToValueAtTime(0.14, now + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.36);

    osc1.connect(gain);
    osc2.connect(gain);
    gain.connect(ctx.destination);

    osc1.start(now);
    osc2.start(now);
    osc1.stop(now + 0.38);
    osc2.stop(now + 0.38);

    setTimeout(() => {
      void ctx.close().catch(() => {});
    }, 500);
  } catch {
    // Ignore audio context autoplay restrictions if user hasn't interacted yet
  }
}

export function ConversationsPanel({ workspaceId }: PanelProps) {
  // Sol sütun filtre & liste state'leri
  const [tab, setTab] = useState<InboxTab>("all");
  const [search, setSearch] = useState("");
  const [sortBy, setSortBy] = useState<InboxSortBy>("priority");
  const [groupBy, setGroupBy] = useState<InboxGroupBy>("none");

  const [counts, setCounts] = useState<InboxCounts>(DEFAULT_COUNTS);
  const [items, setItems] = useState<InboxConversationItem[]>([]);
  const [loadingList, setLoadingList] = useState(true);

  // Seçili konuşma ve detay state'leri
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<InboxConversationDetail | null>(null);
  const [loadingDetail, setLoadingDetail] = useState(false);

  // Orta sütun mesaj & aksiyon state'leri
  const [replyText, setReplyText] = useState("");
  const [sendingReply, setSendingReply] = useState(false);
  const [actionBusy, setActionBusy] = useState<string | null>(null);

  // Sağ sütun Müşteri (Lead) kartı state'leri
  const [contactDisplayName, setContactDisplayName] = useState("");
  const [contactEmail, setContactEmail] = useState("");
  const [contactPhone, setContactPhone] = useState("");
  const [contactTagsInput, setContactTagsInput] = useState("");
  const [contactNotes, setContactNotes] = useState("");
  const [savingContact, setSavingContact] = useState(false);
  const [contactDirty, setContactDirty] = useState(false);

  // Bildirim & Hata state'leri
  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);
  const [soundEnabled, setSoundEnabled] = useState(true);

  const prevUnreadRef = useRef<number | null>(null);
  const prevPendingHandoffRef = useRef<number | null>(null);
  const loadedContactConvIdRef = useRef<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!feedback) return;
    const t = setTimeout(() => setFeedback(null), 3500);
    return () => clearTimeout(t);
  }, [feedback]);

  const syncContactForm = useCallback(
    (contact: InboxContactSummary | undefined, convId: string, force = false) => {
      if (!contact) return;
      if (!force && contactDirty && loadedContactConvIdRef.current === convId) {
        return;
      }
      loadedContactConvIdRef.current = convId;
      setContactDisplayName(contact.displayName ?? "");
      setContactEmail(contact.email ?? "");
      setContactPhone(contact.phone ?? "");
      setContactTagsInput((contact.tags ?? []).join(", "));
      setContactNotes(contact.notes ?? "");
      setContactDirty(false);
    },
    [contactDirty]
  );

  const fetchList = useCallback(
    async (silent = false) => {
      if (!silent) {
        setLoadingList(true);
      }
      try {
        const query = new URLSearchParams({
          tab,
          search: search.trim(),
          sortBy,
          groupBy,
        });
        const data = await api<InboxListResponse>(
          `/workspaces/${workspaceId}/conversations?${query.toString()}`
        );

        const nextCounts = data.counts ?? DEFAULT_COUNTS;
        const nextItems = data.items ?? [];

        // Zil sesi kontrolü (İlk yükleme hariç yeni okunmamış mesaj veya yeni handoff geldiğinde)
        if (
          prevUnreadRef.current !== null &&
          prevPendingHandoffRef.current !== null
        ) {
          const unreadIncreased =
            nextCounts.totalUnread > prevUnreadRef.current;
          const handoffIncreased =
            nextCounts.pendingHandoff > prevPendingHandoffRef.current;
          if ((unreadIncreased || handoffIncreased) && soundEnabled) {
            playNotificationChime();
          }
        }

        prevUnreadRef.current = nextCounts.totalUnread;
        prevPendingHandoffRef.current = nextCounts.pendingHandoff;

        setCounts(nextCounts);
        setItems(nextItems);

        setSelectedId((prevSelected) => {
          if (prevSelected && nextItems.some((i) => i.id === prevSelected)) {
            return prevSelected;
          }
          return nextItems[0]?.id ?? prevSelected;
        });
      } catch (err) {
        if (!silent) {
          setFeedback({
            type: "error",
            text:
              err instanceof ApiError
                ? err.message
                : "Konuşma listesi yüklenemedi.",
          });
        }
      } finally {
        if (!silent) {
          setLoadingList(false);
        }
      }
    },
    [workspaceId, tab, search, sortBy, groupBy, soundEnabled]
  );

  const fetchDetail = useCallback(
    async (conversationId: string, silent = false) => {
      if (!silent) {
        setLoadingDetail(true);
      }
      try {
        const res = await api<InboxConversationDetail>(
          `/workspaces/${workspaceId}/conversations/${conversationId}/detail`
        );
        const resolvedContact: InboxContactSummary = res.contact ??
          res.conversation?.contact ?? {
            id: "",
            displayName: "Ziyaretçi",
            email: null,
            phone: null,
            notes: null,
            tags: [],
          };

        const normalizedDetail: InboxConversationDetail = {
          ...res,
          conversation: {
            ...res.conversation,
            contact: resolvedContact,
          },
          contact: resolvedContact,
          messages: res.messages ?? [],
          members: res.members ?? [],
        };

        setDetail((prev) => {
          const prevCount = prev?.messages?.length ?? 0;
          const nextCount = normalizedDetail.messages.length;
          if (
            !silent ||
            prev?.conversation?.id !== conversationId ||
            nextCount > prevCount
          ) {
            setTimeout(() => {
              messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
            }, 50);
          }
          return normalizedDetail;
        });

        syncContactForm(resolvedContact, conversationId, !silent);
      } catch (err) {
        if (!silent) {
          setFeedback({
            type: "error",
            text:
              err instanceof ApiError
                ? err.message
                : "Konuşma detayı yüklenemedi.",
          });
        }
      } finally {
        if (!silent) {
          setLoadingDetail(false);
        }
      }
    },
    [workspaceId, syncContactForm]
  );

  // İlk yükleme ve filtre değişimleri
  useEffect(() => {
    void fetchList(false);
  }, [fetchList]);

  // Seçili konuşma değiştiğinde detayı getir
  useEffect(() => {
    if (!selectedId) {
      setDetail(null);
      return;
    }
    void fetchDetail(selectedId, false);
  }, [selectedId, fetchDetail]);

  // 4 saniyede bir sessiz gerçek zamanlı polling
  useEffect(() => {
    const interval = setInterval(() => {
      void fetchList(true);
      if (selectedId) {
        void fetchDetail(selectedId, true);
      }
    }, 4000);
    return () => clearInterval(interval);
  }, [fetchList, fetchDetail, selectedId]);

  // Sekme Başlığı (document.title) Güncellemesi
  useEffect(() => {
    if (typeof document === "undefined") return;
    const badgeCount = counts.totalUnread || counts.pendingHandoff;
    if (badgeCount > 0) {
      document.title = `(${badgeCount}) Yeni Mesaj · Daribase`;
    } else {
      document.title = "Daribase · Yapay Zeka Müşteri Destek Platformu";
    }
    return () => {
      document.title = "Daribase · Yapay Zeka Müşteri Destek Platformu";
    };
  }, [counts.totalUnread, counts.pendingHandoff]);

  // Gruplandırılmış konuşma listesi hesaplaması
  const groupedConversations = useMemo(() => {
    if (groupBy === "none") {
      return [{ key: "all", label: "", items }];
    }

    const map = new Map<string, InboxConversationItem[]>();

    for (const item of items) {
      let groupLabel = "Diğer";
      if (groupBy === "status") {
        if (item.status === "RESOLVED") {
          groupLabel = "Çözülenler";
        } else if (item.status === "ARCHIVED") {
          groupLabel = "Arşivlenenler";
        } else if (item.mode === "PENDING_HANDOFF") {
          groupLabel = "Temsilci Bekleyenler (Acil)";
        } else if (item.mode === "HUMAN_ACTIVE") {
          groupLabel = "Temsilci Görüşmesinde";
        } else {
          groupLabel = "AI Yanıtlıyor";
        }
      } else if (groupBy === "channel") {
        groupLabel = item.channelName || "Web Widget";
      } else if (groupBy === "date") {
        const d = new Date(item.lastActivityAt);
        const now = new Date();
        const isToday =
          d.getDate() === now.getDate() &&
          d.getMonth() === now.getMonth() &&
          d.getFullYear() === now.getFullYear();
        const yesterday = new Date(now);
        yesterday.setDate(now.getDate() - 1);
        const isYesterday =
          d.getDate() === yesterday.getDate() &&
          d.getMonth() === yesterday.getMonth() &&
          d.getFullYear() === yesterday.getFullYear();

        if (isToday) groupLabel = "Bugün";
        else if (isYesterday) groupLabel = "Dün";
        else
          groupLabel = d.toLocaleDateString("tr-TR", {
            day: "2-digit",
            month: "long",
            year: "numeric",
          });
      }

      const existing = map.get(groupLabel) ?? [];
      existing.push(item);
      map.set(groupLabel, existing);
    }

    return Array.from(map.entries()).map(([label, groupItems]) => ({
      key: label,
      label,
      items: groupItems,
    }));
  }, [items, groupBy]);

  const activeConversation: InboxConversationItem | null = useMemo(() => {
    if (detail?.conversation && detail.conversation.id === selectedId) {
      return detail.conversation;
    }
    return items.find((i) => i.id === selectedId) ?? null;
  }, [detail, items, selectedId]);

  const parsedTags = useMemo(() => {
    return contactTagsInput
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);
  }, [contactTagsInput]);

  // Temsilci mesaj gönderimi
  const handleSendReply = async (e?: FormEvent) => {
    e?.preventDefault();
    const content = replyText.trim();
    if (!content || !selectedId || sendingReply) return;

    setSendingReply(true);
    setReplyText("");

    const clientMessageId =
      typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
        ? crypto.randomUUID()
        : undefined;

    try {
      const createdMsg = await api<InboxMessageItem>(
        `/workspaces/${workspaceId}/conversations/${selectedId}/messages`,
        {
          method: "POST",
          body: JSON.stringify({
            content,
            clientMessageId,
          }),
        }
      );

      setDetail((prev) => {
        if (!prev) return prev;
        return {
          ...prev,
          conversation: {
            ...prev.conversation,
            mode: "HUMAN_ACTIVE",
            lastMessagePreview: content,
            lastActivityAt: new Date().toISOString(),
          },
          messages: [...prev.messages, createdMsg],
        };
      });

      setTimeout(() => {
        messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
      }, 40);

      void fetchList(true);
      void fetchDetail(selectedId, true);
    } catch (err) {
      setReplyText(content);
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "Mesaj gönderilirken bir hata oluştu.",
      });
    } finally {
      setSendingReply(false);
    }
  };

  const handleTextareaKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void handleSendReply();
    }
  };

  // Konuşma durum / mod aksiyonları (takeover, handback-to-ai, resolve, archive, unarchive)
  const runConversationAction = async (
    actionEndpoint:
      | "takeover"
      | "handback-to-ai"
      | "resolve"
      | "archive"
      | "unarchive",
    successMsg: string
  ) => {
    if (!selectedId || actionBusy) return;
    setActionBusy(actionEndpoint);
    try {
      await api(
        `/workspaces/${workspaceId}/conversations/${selectedId}/${actionEndpoint}`,
        {
          method: "POST",
          body: JSON.stringify({}),
        }
      );
      setFeedback({ type: "success", text: successMsg });
      await Promise.all([fetchList(true), fetchDetail(selectedId, true)]);
    } catch (err) {
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "İşlem gerçekleştirilemedi.",
      });
    } finally {
      setActionBusy(null);
    }
  };

  // Sağ sütun Müşteri & Lead Kartı kaydetme
  const handleSaveContact = async (e: FormEvent) => {
    e.preventDefault();
    if (!selectedId || savingContact) return;

    setSavingContact(true);
    try {
      const updatedContact = await api<InboxContactSummary>(
        `/workspaces/${workspaceId}/conversations/${selectedId}/contact`,
        {
          method: "PUT",
          body: JSON.stringify({
            displayName: contactDisplayName.trim() || "Ziyaretçi",
            email: contactEmail.trim() || null,
            phone: contactPhone.trim() || null,
            notes: contactNotes.trim() || null,
            tags: parsedTags,
          }),
        }
      );

      setContactDirty(false);
      syncContactForm(updatedContact, selectedId, true);
      setFeedback({
        type: "success",
        text: "Müşteri ve lead bilgileri kaydedildi.",
      });
      await Promise.all([fetchList(true), fetchDetail(selectedId, true)]);
    } catch (err) {
      setFeedback({
        type: "error",
        text:
          err instanceof ApiError
            ? err.message
            : "Müşteri bilgileri kaydedilemedi.",
      });
    } finally {
      setSavingContact(false);
    }
  };

  return (
    <div className="grid grid-cols-1 lg:grid-cols-[360px_1fr_320px] h-[calc(100vh-3.5rem)] overflow-hidden bg-white">
      {/* ===================================================================
          1. SOL SÜTUN: KONUŞMA LİSTESİ, SEKMELER, SIRALAMA & GRUPLAMA
         =================================================================== */}
      <div className="border-r border-slate-200 flex flex-col h-full bg-slate-50/40 overflow-hidden">
        {/* Üst Arama ve Filtre Başlığı */}
        <div className="p-3.5 border-b border-slate-200 bg-white space-y-3 shrink-0">
          <div className="flex items-center justify-between gap-2">
            <div className="flex items-center gap-2">
              <MessageSquare className="w-4 h-4 text-[#7c3aed]" />
              <span className="text-sm font-bold text-slate-900">
                Gelen Kutusu
              </span>
              {counts.totalUnread > 0 && (
                <Badge className="bg-[#7c3aed] text-white text-[10px] px-2 py-0">
                  {counts.totalUnread} yeni
                </Badge>
              )}
            </div>

            <div className="flex items-center gap-1">
              <Button
                type="button"
                variant="ghost"
                size="icon"
                title={
                  soundEnabled
                    ? "Bildirim Sesi Açık (Test etmek veya kapatmak için tıklayın)"
                    : "Bildirim Sesi Kapalı"
                }
                onClick={() => {
                  const next = !soundEnabled;
                  setSoundEnabled(next);
                  if (next) playNotificationChime();
                }}
                className={`h-7 w-7 rounded-lg ${
                  soundEnabled ? "text-[#7c3aed]" : "text-slate-400"
                }`}
              >
                <Volume2 className="w-3.5 h-3.5" />
              </Button>

              <Button
                type="button"
                variant="ghost"
                size="icon"
                title="Listeyi Yenile"
                onClick={() => {
                  void fetchList(false);
                  if (selectedId) void fetchDetail(selectedId, true);
                }}
                className="h-7 w-7 rounded-lg text-slate-500 hover:text-slate-900"
              >
                <RefreshCw
                  className={`w-3.5 h-3.5 ${loadingList ? "animate-spin" : ""}`}
                />
              </Button>
            </div>
          </div>

          {/* Arama Çubuğu */}
          <div className="relative">
            <Search className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="İsim, e-posta veya mesaj ara..."
              className="pl-9 h-9 text-xs rounded-xl bg-slate-50/70 border-slate-200 focus:bg-white"
            />
          </div>

          {/* 6 Filtre Sekmesi (Kaydırılabilir Hap/Pill Butonlar - Sayaçlı) */}
          <Tabs
            value={tab}
            onValueChange={(v) => setTab(v as InboxTab)}
            className="w-full"
          >
            <TabsList className="w-full flex items-center justify-start gap-1 overflow-x-auto bg-slate-100/80 p-1 rounded-xl h-auto no-scrollbar">
              <TabsTrigger
                value="all"
                className="text-[11px] px-2.5 py-1.5 rounded-lg shrink-0"
              >
                Tümü ({counts.all})
              </TabsTrigger>

              <TabsTrigger
                value="pending_handoff"
                className={`text-[11px] px-2.5 py-1.5 rounded-lg shrink-0 gap-1 ${
                  counts.pendingHandoff > 0
                    ? "data-[state=inactive]:bg-orange-50 data-[state=inactive]:text-orange-700 data-[state=active]:bg-orange-600 data-[state=active]:text-white font-semibold"
                    : ""
                }`}
              >
                <AlertTriangle className="w-3 h-3 shrink-0" />
                Temsilci Bekleyen ({counts.pendingHandoff})
              </TabsTrigger>

              <TabsTrigger
                value="bot_active"
                className="text-[11px] px-2.5 py-1.5 rounded-lg shrink-0 gap-1"
              >
                <Bot className="w-3 h-3 shrink-0" />
                AI Yanıtlıyor ({counts.botActive})
              </TabsTrigger>

              <TabsTrigger
                value="assigned_to_me"
                className="text-[11px] px-2.5 py-1.5 rounded-lg shrink-0 gap-1"
              >
                <User className="w-3 h-3 shrink-0" />
                Bana Atananlar ({counts.assignedToMe})
              </TabsTrigger>

              <TabsTrigger
                value="resolved"
                className="text-[11px] px-2.5 py-1.5 rounded-lg shrink-0 gap-1"
              >
                <CheckCircle2 className="w-3 h-3 shrink-0" />
                Çözülenler ({counts.resolved})
              </TabsTrigger>

              <TabsTrigger
                value="archived"
                className="text-[11px] px-2.5 py-1.5 rounded-lg shrink-0 gap-1"
              >
                <Archive className="w-3 h-3 shrink-0" />
                Arşiv ({counts.archived})
              </TabsTrigger>
            </TabsList>
          </Tabs>

          {/* Sıralama ve Gruplandırma Çubuğu */}
          <div className="flex items-center gap-2">
            <div className="flex-1 min-w-0">
              <Select
                value={sortBy}
                onValueChange={(v) => setSortBy(v as InboxSortBy)}
              >
                <SelectTrigger className="h-8 text-[11px] rounded-lg px-2.5 bg-slate-50 border-slate-200">
                  <div className="flex items-center gap-1.5 truncate">
                    <ArrowUpDown className="w-3 h-3 text-slate-400 shrink-0" />
                    <SelectValue>{SORT_LABELS[sortBy]}</SelectValue>
                  </div>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="priority" className="text-xs">
                    Öncelik (Acil Üstte)
                  </SelectItem>
                  <SelectItem value="newest" className="text-xs">
                    En Yeni
                  </SelectItem>
                  <SelectItem value="oldest" className="text-xs">
                    En Eski
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>

            <div className="flex-1 min-w-0">
              <Select
                value={groupBy}
                onValueChange={(v) => setGroupBy(v as InboxGroupBy)}
              >
                <SelectTrigger className="h-8 text-[11px] rounded-lg px-2.5 bg-slate-50 border-slate-200">
                  <div className="flex items-center gap-1.5 truncate">
                    <Layers className="w-3 h-3 text-slate-400 shrink-0" />
                    <SelectValue>{GROUP_LABELS[groupBy]}</SelectValue>
                  </div>
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="none" className="text-xs">
                    Gruplama Yok
                  </SelectItem>
                  <SelectItem value="status" className="text-xs">
                    Duruma Göre
                  </SelectItem>
                  <SelectItem value="channel" className="text-xs">
                    Kanala Göre
                  </SelectItem>
                  <SelectItem value="date" className="text-xs">
                    Tarihe Göre
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
        </div>

        {/* Konuşma Listesi */}
        <div className="flex-1 overflow-y-auto divide-y divide-slate-100">
          {loadingList && items.length === 0 ? (
            <div className="p-8 flex flex-col items-center justify-center text-slate-400 gap-2">
              <Loader2 className="w-6 h-6 animate-spin text-[#7c3aed]" />
              <span className="text-xs">Konuşmalar yükleniyor...</span>
            </div>
          ) : items.length === 0 ? (
            <div className="p-8 text-center space-y-2">
              <div className="w-10 h-10 rounded-2xl bg-slate-100 text-slate-400 flex items-center justify-center mx-auto">
                <MessageSquare className="w-5 h-5" />
              </div>
              <p className="text-xs font-semibold text-slate-700">
                Bu sekmede konuşma bulunamadı
              </p>
              <p className="text-[11px] text-slate-400">
                Canlı destek widget&apos;ınızdan gelen mesajlar anında burada
                listelenir.
              </p>
            </div>
          ) : (
            groupedConversations.map((group) => (
              <div key={group.key}>
                {groupBy !== "none" && group.label && (
                  <div className="bg-slate-100/80 px-4 py-1.5 text-[11px] font-semibold text-slate-600 uppercase tracking-wider sticky top-0 z-10 border-b border-slate-200/60">
                    {group.label} ({group.items.length})
                  </div>
                )}

                <div className="divide-y divide-slate-100">
                  {group.items.map((conv) => {
                    const isSelected = conv.id === selectedId;
                    const isPendingHandoff =
                      conv.status === "OPEN" &&
                      conv.mode === "PENDING_HANDOFF";

                    return (
                      <button
                        key={conv.id}
                        type="button"
                        onClick={() => setSelectedId(conv.id)}
                        className={`w-full text-left p-3.5 transition-colors cursor-pointer block ${
                          isPendingHandoff
                            ? "border-l-4 border-orange-500 bg-orange-50/30 hover:bg-orange-50/60"
                            : isSelected
                            ? "bg-violet-50/70 border-l-4 border-[#7c3aed]"
                            : "bg-white hover:bg-slate-50"
                        }`}
                      >
                        {/* Üst Satır: Müşteri Adı ve Zaman */}
                        <div className="flex items-center justify-between gap-2 mb-1.5">
                          <span className="font-semibold text-xs text-slate-900 truncate">
                            {conv.contact?.displayName || "Ziyaretçi"}
                          </span>
                          <span className="text-[11px] text-slate-400 shrink-0">
                            {formatShortTime(conv.lastActivityAt)}
                          </span>
                        </div>

                        {/* Orta Satır: Durum / Mod Rozetleri */}
                        <div className="flex flex-wrap items-center gap-1.5 mb-2">
                          {conv.status === "ARCHIVED" ? (
                            <Badge
                              variant="secondary"
                              className="text-[10px] px-2 py-0 bg-slate-100 text-slate-600"
                            >
                              <Archive className="w-3 h-3 mr-1" />
                              Arşivlendi
                            </Badge>
                          ) : conv.status === "RESOLVED" ? (
                            <>
                              <Badge
                                variant="secondary"
                                className="text-[10px] px-2 py-0 bg-emerald-50 text-emerald-700 border-emerald-200"
                              >
                                <CheckCircle2 className="w-3 h-3 mr-1" />
                                Çözüldü
                              </Badge>
                              <span className="inline-flex items-center gap-1 text-[10px] text-slate-400">
                                <Clock className="w-3 h-3" />
                                6s içinde arşivlenir
                              </span>
                            </>
                          ) : conv.mode === "PENDING_HANDOFF" ? (
                            <Badge
                              variant="secondary"
                              className="text-[10px] px-2 py-0 bg-orange-100 text-orange-800 border-orange-300 animate-pulse"
                            >
                              <AlertTriangle className="w-3 h-3 mr-1" />
                              Temsilci Bekliyor
                            </Badge>
                          ) : conv.mode === "HUMAN_ACTIVE" ? (
                            <Badge
                              variant="secondary"
                              className="text-[10px] px-2 py-0 bg-blue-50 text-blue-700 border-blue-200"
                            >
                              <UserCheck className="w-3 h-3 mr-1" />
                              Temsilci: {conv.assignedUserName || "Aktif"}
                            </Badge>
                          ) : (
                            <Badge
                              variant="secondary"
                              className="text-[10px] px-2 py-0 bg-violet-50 text-[#7c3aed] border-violet-200"
                            >
                              <Bot className="w-3 h-3 mr-1" />
                              AI Yanıtlıyor
                            </Badge>
                          )}

                          {conv.contact?.tags?.slice(0, 2).map((t) => (
                            <Badge
                              key={t}
                              variant="outline"
                              className="text-[9px] px-1.5 py-0 text-slate-500 border-slate-200"
                            >
                              {t}
                            </Badge>
                          ))}
                        </div>

                        {/* Alt Satır: Son Mesaj Önizlemesi ve Okunmamış Sayacı */}
                        <div className="flex items-center justify-between gap-2">
                          <p className="text-xs text-slate-500 truncate flex-1">
                            {conv.lastMessagePreview ||
                              "Henüz mesaj gönderilmedi."}
                          </p>
                          {conv.unreadCount > 0 && (
                            <span className="inline-flex items-center justify-center min-w-5 h-5 px-1.5 rounded-full bg-[#7c3aed] text-white text-[10px] font-bold shrink-0">
                              {conv.unreadCount}
                            </span>
                          )}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>
            ))
          )}
        </div>
      </div>

      {/* ===================================================================
          2. ORTA SÜTUN: CANLI SOHBET AKIŞI VE MOD KONTROL BARI
         =================================================================== */}
      <div className="flex flex-col h-full bg-white overflow-hidden">
        {!activeConversation ? (
          <div className="flex-1 flex flex-col items-center justify-center p-8 text-center bg-slate-50/20">
            <div className="w-12 h-12 rounded-2xl bg-violet-50 text-[#7c3aed] flex items-center justify-center mb-3">
              <Sparkles className="w-6 h-6" />
            </div>
            <h3 className="text-sm font-bold text-slate-900">
              Bir Sohbet Seçin
            </h3>
            <p className="text-xs text-slate-500 max-w-xs mt-1">
              Mesaj geçmişini görüntülemek, canlı müdahale etmek veya müşteri
              kartını düzenlemek için sol listeden bir konuşma seçin.
            </p>
          </div>
        ) : (
          <>
            {/* Üst Aksiyon Barı */}
            <div className="px-5 py-3 border-b border-slate-200 flex flex-wrap items-center justify-between gap-3 shrink-0 bg-white">
              <div className="flex items-center gap-3 min-w-0">
                <Avatar className="h-9 w-9 border border-slate-200">
                  <AvatarFallback className="bg-violet-50 text-[#7c3aed] text-xs font-bold">
                    {getInitials(activeConversation.contact?.displayName)}
                  </AvatarFallback>
                </Avatar>
                <div className="min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <h2 className="text-sm font-bold text-slate-900 truncate">
                      {activeConversation.contact?.displayName || "Ziyaretçi"}
                    </h2>
                    <Badge
                      variant="outline"
                      className="text-[10px] border-slate-200 text-slate-600"
                    >
                      {activeConversation.channelName || "Web Sitesi"}
                    </Badge>
                    {activeConversation.status === "OPEN" &&
                      activeConversation.mode === "PENDING_HANDOFF" && (
                        <Badge
                          variant="secondary"
                          className="text-[10px] bg-orange-100 text-orange-800 border-orange-300"
                        >
                          <AlertTriangle className="w-3 h-3 mr-1" />
                          Temsilci Bekliyor
                        </Badge>
                      )}
                    {activeConversation.status === "OPEN" &&
                      activeConversation.mode === "BOT_ACTIVE" && (
                        <Badge
                          variant="secondary"
                          className="text-[10px] bg-violet-50 text-[#7c3aed] border-violet-200"
                        >
                          <Bot className="w-3 h-3 mr-1" />
                          AI Yanıtlıyor
                        </Badge>
                      )}
                    {activeConversation.status === "OPEN" &&
                      activeConversation.mode === "HUMAN_ACTIVE" && (
                        <Badge
                          variant="secondary"
                          className="text-[10px] bg-blue-50 text-blue-700 border-blue-200"
                        >
                          <UserCheck className="w-3 h-3 mr-1" />
                          Temsilci:{" "}
                          {activeConversation.assignedUserName || "Aktif"}
                        </Badge>
                      )}
                  </div>
                </div>
              </div>

              {/* Sağ Aksiyon Butonları */}
              <div className="flex items-center gap-2 flex-wrap">
                {activeConversation.mode !== "HUMAN_ACTIVE" &&
                  activeConversation.status === "OPEN" && (
                    <Button
                      type="button"
                      size="sm"
                      disabled={Boolean(actionBusy)}
                      onClick={() =>
                        void runConversationAction(
                          "takeover",
                          "Sohbet devralındı. AI asistanı bu konuşma için susturuldu."
                        )
                      }
                      className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white h-8 text-xs rounded-xl"
                    >
                      <UserCheck className="w-3.5 h-3.5 mr-1.5" />
                      Sohbeti Devral
                    </Button>
                  )}

                {activeConversation.mode === "HUMAN_ACTIVE" &&
                  activeConversation.status === "OPEN" && (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={Boolean(actionBusy)}
                      onClick={() =>
                        void runConversationAction(
                          "handback-to-ai",
                          "Sohbet tekrar AI asistanına devredildi."
                        )
                      }
                      className="h-8 text-xs rounded-xl border-slate-200"
                    >
                      <Bot className="w-3.5 h-3.5 mr-1.5" />
                      AI&apos;a Geri Devret
                    </Button>
                  )}

                {activeConversation.status === "OPEN" && (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={Boolean(actionBusy)}
                    onClick={() =>
                      void runConversationAction(
                        "resolve",
                        "Sohbet çözüldü olarak işaretlendi. 6 saat sonra otomatik arşivlenecek."
                      )
                    }
                    className="text-emerald-700 border-emerald-200 hover:bg-emerald-50 h-8 text-xs rounded-xl"
                  >
                    <CheckCircle2 className="w-3.5 h-3.5 mr-1.5" />
                    Çözüldü İşaretle
                  </Button>
                )}

                {activeConversation.status !== "ARCHIVED" && (
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={Boolean(actionBusy)}
                    onClick={() =>
                      void runConversationAction(
                        "archive",
                        "Sohbet arşivlendi."
                      )
                    }
                    className="h-8 text-xs rounded-xl text-slate-600 hover:text-slate-900"
                  >
                    <Archive className="w-3.5 h-3.5 mr-1.5" />
                    Arşivle
                  </Button>
                )}

                {activeConversation.status === "ARCHIVED" && (
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled={Boolean(actionBusy)}
                    onClick={() =>
                      void runConversationAction(
                        "unarchive",
                        "Sohbet arşivden çıkarıldı."
                      )
                    }
                    className="h-8 text-xs rounded-xl border-slate-200"
                  >
                    <ArchiveRestore className="w-3.5 h-3.5 mr-1.5" />
                    Arşivden Çıkar
                  </Button>
                )}
              </div>
            </div>

            {/* Geri Bildirim Alert */}
            {feedback && (
              <div className="px-5 pt-2 shrink-0">
                <Alert
                  variant={
                    feedback.type === "error" ? "destructive" : "default"
                  }
                  className={
                    feedback.type === "success"
                      ? "py-2 border-emerald-200 bg-emerald-50 text-emerald-900 rounded-xl"
                      : "py-2 rounded-xl"
                  }
                >
                  <AlertDescription className="text-xs font-medium">
                    {feedback.text}
                  </AlertDescription>
                </Alert>
              </div>
            )}

            {/* Durum Bilgi Bandı (Banner) */}
            {activeConversation.status === "OPEN" &&
              activeConversation.mode === "PENDING_HANDOFF" && (
                <div className="px-5 py-2.5 bg-orange-50 border-b border-orange-200 text-xs text-orange-900 flex items-center gap-2 shrink-0 font-medium">
                  <AlertTriangle className="w-4 h-4 text-orange-600 shrink-0" />
                  <span>
                    Müşteri canlı temsilci bekliyor! Yanıt yazdığınızda veya
                    &apos;Sohbeti Devral&apos;a bastığınızda AI susacak ve
                    kontrol size geçecektir.
                  </span>
                </div>
              )}

            {activeConversation.status === "RESOLVED" && (
              <div className="px-5 py-2.5 bg-emerald-50/70 border-b border-emerald-200 text-xs text-emerald-900 flex items-center gap-2 shrink-0 font-medium">
                <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
                <span>
                  Bu sohbet çözüldü olarak işaretlendi ve yeni mesaj alımına
                  kapatıldı. Çözüldükten 6 saat sonra otomatik olarak Arşiv
                  sekmesine taşınır.
                </span>
              </div>
            )}

            {/* Mesaj Geçmişi */}
            <div className="flex-1 overflow-y-auto p-5 space-y-4 bg-slate-50/30">
              {loadingDetail && !detail ? (
                <div className="h-full flex items-center justify-center text-slate-400 gap-2">
                  <Loader2 className="w-5 h-5 animate-spin text-[#7c3aed]" />
                  <span className="text-xs">Mesaj geçmişi yükleniyor...</span>
                </div>
              ) : !detail?.messages || detail.messages.length === 0 ? (
                <div className="h-full flex flex-col items-center justify-center text-center text-slate-400">
                  <p className="text-xs">Bu konuşmada henüz mesaj bulunmuyor.</p>
                </div>
              ) : (
                detail.messages.map((msg) => {
                  if (msg.senderKind === "GUEST") {
                    return (
                      <div key={msg.id} className="flex flex-col items-start">
                        <span className="text-[11px] font-medium text-slate-500 mb-1 ml-1">
                          {activeConversation.contact?.displayName ||
                            "Ziyaretçi"}{" "}
                          · {formatShortTime(msg.createdAt)}
                        </span>
                        <div className="bg-white border border-slate-200 text-slate-800 rounded-2xl rounded-tl-sm px-4 py-2.5 text-sm max-w-[78%] shadow-2xs whitespace-pre-wrap break-words leading-relaxed">
                          {msg.content}
                        </div>
                      </div>
                    );
                  }

                  if (msg.senderKind === "AI") {
                    return (
                      <div key={msg.id} className="flex flex-col items-end">
                        <span className="text-[11px] font-semibold text-[#7c3aed] mb-1 mr-1 inline-flex items-center gap-1">
                          <Bot className="w-3.5 h-3.5" />
                          AI Asistanı · {formatShortTime(msg.createdAt)}
                        </span>
                        <div className="bg-violet-50 border border-violet-200 text-slate-900 rounded-2xl rounded-tr-sm px-4 py-2.5 text-sm max-w-[78%] shadow-2xs whitespace-pre-wrap break-words leading-relaxed">
                          {msg.content}
                        </div>
                      </div>
                    );
                  }

                  return (
                    <div key={msg.id} className="flex flex-col items-end">
                      <span className="text-[11px] font-semibold text-slate-600 mb-1 mr-1 inline-flex items-center gap-1">
                        <User className="w-3.5 h-3.5" />
                        {msg.senderName || "Temsilci"} ·{" "}
                        {formatShortTime(msg.createdAt)}
                      </span>
                      <div className="bg-[#7c3aed] text-white rounded-2xl rounded-tr-sm px-4 py-2.5 text-sm max-w-[78%] shadow-xs whitespace-pre-wrap break-words leading-relaxed">
                        {msg.content}
                      </div>
                    </div>
                  );
                })
              )}
              <div ref={messagesEndRef} />
            </div>

            {/* Alt Yanıt Yazma Alanı */}
            <div className="p-4 border-t border-slate-200 bg-white shrink-0">
              {activeConversation.status === "RESOLVED" ||
              activeConversation.status === "ARCHIVED" ? (
                <div className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 text-xs text-slate-500">
                  <Archive className="w-4 h-4 text-slate-400 shrink-0" />
                  <span>
                    Kapalı veya arşivlenmiş sohbetlere mesaj gönderilemez.
                  </span>
                </div>
              ) : (
                <form
                  onSubmit={handleSendReply}
                  className="flex items-end gap-2.5"
                >
                  <Textarea
                    rows={2}
                    value={replyText}
                    onChange={(e) => setReplyText(e.target.value)}
                    onKeyDown={handleTextareaKeyDown}
                    disabled={sendingReply}
                    placeholder="Müşteriye yanıt yazın (Gönderdiğinizde AI otomatik susar ve sohbeti devralırsınız)..."
                    className="min-h-[58px] max-h-32 resize-none rounded-xl border-slate-200 bg-slate-50/50 focus:bg-white text-sm"
                  />
                  <Button
                    type="submit"
                    disabled={!replyText.trim() || sendingReply}
                    className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white rounded-xl h-[58px] px-5 shrink-0 font-semibold"
                  >
                    {sendingReply ? (
                      <Loader2 className="w-4 h-4 animate-spin" />
                    ) : (
                      <>
                        <Send className="w-4 h-4 mr-1.5" />
                        Gönder
                      </>
                    )}
                  </Button>
                </form>
              )}
            </div>
          </>
        )}
      </div>

      {/* ===================================================================
          3. SAĞ SÜTUN: MÜŞTERİ / ZİYARETÇİ (LEAD) DETAY KARTI
         =================================================================== */}
      <div className="border-l border-slate-200 bg-white flex flex-col h-full overflow-y-auto p-5 space-y-5">
        {!activeConversation ? (
          <div className="h-full flex flex-col items-center justify-center text-center text-slate-400">
            <User className="w-8 h-8 mb-2 stroke-1" />
            <p className="text-xs">Müşteri detayları burada görünür.</p>
          </div>
        ) : (
          <>
            {/* Üstte Avatar ve Müşteri & Lead Bilgileri Başlığı */}
            <div className="flex items-center gap-3">
              <Avatar className="h-11 w-11 border border-violet-200">
                <AvatarFallback className="bg-violet-50 text-[#7c3aed] font-bold text-sm">
                  {getInitials(contactDisplayName || activeConversation.contact?.displayName)}
                </AvatarFallback>
              </Avatar>
              <div className="min-w-0">
                <h3 className="text-sm font-bold text-slate-900 truncate">
                  Müşteri &amp; Lead Bilgileri
                </h3>
                <p className="text-[11px] text-slate-500 truncate">
                  Ziyaretçi iletişim ve CRM kartı
                </p>
              </div>
            </div>

            <Separator />

            {/* Düzenlenebilir Form Alanları */}
            <form onSubmit={handleSaveContact} className="space-y-3.5">
              <div className="space-y-1.5">
                <Label className="text-xs font-semibold text-slate-700">
                  Ad Soyad / Görünen Ad
                </Label>
                <div className="relative">
                  <User className="w-3.5 h-3.5 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
                  <Input
                    value={contactDisplayName}
                    onChange={(e) => {
                      setContactDisplayName(e.target.value);
                      setContactDirty(true);
                    }}
                    placeholder="Ziyaretçi adı..."
                    className="pl-8 h-9 text-xs rounded-xl"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <Label className="text-xs font-semibold text-slate-700">
                  E-posta Adresi
                </Label>
                <div className="relative">
                  <Mail className="w-3.5 h-3.5 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
                  <Input
                    type="email"
                    value={contactEmail}
                    onChange={(e) => {
                      setContactEmail(e.target.value);
                      setContactDirty(true);
                    }}
                    placeholder="ornek@sirket.com"
                    className="pl-8 h-9 text-xs rounded-xl"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <Label className="text-xs font-semibold text-slate-700">
                  Telefon Numarası
                </Label>
                <div className="relative">
                  <Phone className="w-3.5 h-3.5 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
                  <Input
                    value={contactPhone}
                    onChange={(e) => {
                      setContactPhone(e.target.value);
                      setContactDirty(true);
                    }}
                    placeholder="+90 5XX XXX XX XX"
                    className="pl-8 h-9 text-xs rounded-xl"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <Label className="text-xs font-semibold text-slate-700">
                  Etiketler (Virgülle ayırın)
                </Label>
                <div className="relative">
                  <Tag className="w-3.5 h-3.5 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none" />
                  <Input
                    value={contactTagsInput}
                    onChange={(e) => {
                      setContactTagsInput(e.target.value);
                      setContactDirty(true);
                    }}
                    placeholder="VIP, Sağlık Turizmi, Acil"
                    className="pl-8 h-9 text-xs rounded-xl"
                  />
                </div>
                {parsedTags.length > 0 && (
                  <div className="flex flex-wrap gap-1.5 pt-1">
                    {parsedTags.map((tag) => (
                      <Badge
                        key={tag}
                        variant="secondary"
                        className="text-[10px] bg-violet-50 text-[#7c3aed] border-violet-200"
                      >
                        {tag}
                      </Badge>
                    ))}
                  </div>
                )}
              </div>

              <div className="space-y-1.5">
                <Label className="text-xs font-semibold text-slate-700 flex items-center gap-1.5">
                  <FileText className="w-3.5 h-3.5 text-slate-400" />
                  Ekip İçi Özel Notlar
                </Label>
                <Textarea
                  rows={3}
                  value={contactNotes}
                  onChange={(e) => {
                    setContactNotes(e.target.value);
                    setContactDirty(true);
                  }}
                  placeholder="Müşteri hakkında ekip içi notlar..."
                  className="text-xs rounded-xl resize-none"
                />
              </div>

              <Button
                type="submit"
                disabled={savingContact}
                className="w-full bg-slate-900 hover:bg-slate-800 text-white rounded-xl h-9 text-xs font-semibold"
              >
                {savingContact ? (
                  <>
                    <Loader2 className="w-3.5 h-3.5 mr-1.5 animate-spin" />
                    Kaydediliyor...
                  </>
                ) : (
                  "Müşteri Bilgilerini Kaydet"
                )}
              </Button>
            </form>

            <Separator />

            {/* Oturum Meta Bilgileri Kartı */}
            <Card className="rounded-2xl border-slate-200/80 bg-slate-50/60 shadow-none">
              <CardContent className="p-4 space-y-2.5 text-xs">
                <div className="font-semibold text-slate-800 flex items-center gap-1.5 mb-1">
                  <Clock className="w-3.5 h-3.5 text-[#7c3aed]" />
                  Oturum Meta Bilgileri
                </div>

                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-500">Kanal</span>
                  <span className="font-medium text-slate-800">
                    {activeConversation.channelName || "Web Widget"}
                  </span>
                </div>

                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-500">Başlangıç Tarihi</span>
                  <span className="font-medium text-slate-800">
                    {formatFullDateTime(activeConversation.createdAt)}
                  </span>
                </div>

                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-500">Son Aktivite</span>
                  <span className="font-medium text-slate-800">
                    {formatFullDateTime(activeConversation.lastActivityAt)}
                  </span>
                </div>

                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-500">Çözülme Tarihi</span>
                  <span className="font-medium text-slate-800">
                    {formatFullDateTime(activeConversation.resolvedAt)}
                  </span>
                </div>

                <div className="flex items-center justify-between gap-2">
                  <span className="text-slate-500">Otomatik Arşiv Zamanı</span>
                  <span className="font-medium text-slate-800">
                    {formatFullDateTime(activeConversation.autoArchiveAt)}
                  </span>
                </div>
              </CardContent>
            </Card>
          </>
        )}
      </div>
    </div>
  );
}
