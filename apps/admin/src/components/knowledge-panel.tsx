"use client";

import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from "react";
import {
  AlertCircle,
  BookOpen,
  CheckCircle2,
  Database,
  FileText,
  Globe,
  HardDrive,
  Link2,
  RefreshCw,
  Search,
  ShieldAlert,
  Sparkles,
  Trash2,
  UploadCloud,
  Users,
} from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { api, apiUpload, errorMessage } from "@/lib/api";
import type {
  KnowledgeBaseOverview,
  KnowledgeDocumentItem,
  PanelProps,
} from "@/lib/types";

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const index = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const value = bytes / Math.pow(1024, index);
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleDateString("tr-TR", {
      day: "2-digit",
      month: "short",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return iso;
  }
}

export function KnowledgePanel({ workspaceId, onSessionRefresh }: PanelProps) {
  const [data, setData] = useState<KnowledgeBaseOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");

  // 4 AI Kural Parametresi state
  const [businessSummary, setBusinessSummary] = useState("");
  const [keyFaq, setKeyFaq] = useState("");
  const [answerBoundaries, setAnswerBoundaries] = useState("");
  const [escalationRules, setEscalationRules] = useState("");
  const [savingRules, setSavingRules] = useState(false);

  // Kaynak ekleme state
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const [uploadingFile, setUploadingFile] = useState(false);

  const [textTitle, setTextTitle] = useState("");
  const [textContent, setTextContent] = useState("");
  const [addingText, setAddingText] = useState(false);

  const [urlTitle, setUrlTitle] = useState("");
  const [urlValue, setUrlValue] = useState("");
  const [addingUrl, setAddingUrl] = useState(false);

  // Tablo seçim ve aksiyon state
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [busyDocId, setBusyDocId] = useState<string | null>(null);
  const [bulkDeleting, setBulkDeleting] = useState(false);

  // Bildirim state
  const [feedback, setFeedback] = useState<{ text: string; type: "success" | "error" } | null>(
    null
  );

  useEffect(() => {
    if (!feedback) return;
    const timer = setTimeout(() => setFeedback(null), 4500);
    return () => clearTimeout(timer);
  }, [feedback]);

  const loadOverview = useCallback(
    async (searchQuery: string, syncRules = false) => {
      try {
        const res = await api<KnowledgeBaseOverview>(
          `/workspaces/${workspaceId}/knowledge-base?search=${encodeURIComponent(searchQuery)}`
        );
        setData(res);
        if (syncRules) {
          setBusinessSummary(res.rules?.businessSummary ?? "");
          setKeyFaq(res.rules?.keyFaq ?? "");
          setAnswerBoundaries(res.rules?.answerBoundaries ?? "");
          setEscalationRules(res.rules?.escalationRules ?? "");
        }
      } catch (err) {
        setFeedback({ text: errorMessage(err), type: "error" });
      } finally {
        setLoading(false);
      }
    },
    [workspaceId]
  );

  useEffect(() => {
    setLoading(true);
    void loadOverview("", true);
  }, [loadOverview]);

  useEffect(() => {
    const timer = setTimeout(() => {
      void loadOverview(search, false);
    }, 250);
    return () => clearTimeout(timer);
  }, [search, loadOverview]);

  async function handleSaveRules() {
    if (savingRules) return;
    setSavingRules(true);
    setFeedback(null);
    try {
      await api(`/workspaces/${workspaceId}/knowledge-base/rules`, {
        method: "PUT",
        body: JSON.stringify({
          businessSummary,
          keyFaq,
          answerBoundaries,
          escalationRules,
        }),
      });
      setFeedback({
        text: "AI davranış kuralları kaydedildi ve asistan talimatları güncellendi.",
        type: "success",
      });
      await loadOverview(search, false);
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setSavingRules(false);
    }
  }

  async function handleFileChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file || uploadingFile) return;

    setUploadingFile(true);
    setFeedback(null);
    try {
      await apiUpload<KnowledgeDocumentItem>(
        `/workspaces/${workspaceId}/knowledge-base/files`,
        file
      );
      setFeedback({
        text: `"${file.name}" başarıyla yüklendi ve indekslendi.`,
        type: "success",
      });
      await loadOverview(search, false);
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setUploadingFile(false);
    }
  }

  async function handleAddText(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (addingText || !textTitle.trim() || !textContent.trim()) return;

    setAddingText(true);
    setFeedback(null);
    try {
      await api<KnowledgeDocumentItem>(`/workspaces/${workspaceId}/knowledge-base/texts`, {
        method: "POST",
        body: JSON.stringify({
          title: textTitle.trim(),
          content: textContent.trim(),
        }),
      });
      setTextTitle("");
      setTextContent("");
      setFeedback({
        text: "Metin kaynağı başarıyla eklendi ve indekslendi.",
        type: "success",
      });
      await loadOverview(search, false);
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setAddingText(false);
    }
  }

  async function handleAddUrl(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (addingUrl || !urlValue.trim()) return;

    setAddingUrl(true);
    setFeedback(null);
    try {
      await api<KnowledgeDocumentItem>(`/workspaces/${workspaceId}/knowledge-base/urls`, {
        method: "POST",
        body: JSON.stringify({
          title: urlTitle.trim() || undefined,
          url: urlValue.trim(),
        }),
      });
      setUrlTitle("");
      setUrlValue("");
      setFeedback({
        text: "Web linki başarıyla tarandı ve bilgi bankasına eklendi.",
        type: "success",
      });
      await loadOverview(search, false);
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setAddingUrl(false);
    }
  }

  async function handleReindex(id: string) {
    if (busyDocId) return;
    setBusyDocId(id);
    setFeedback(null);
    try {
      await api(`/workspaces/${workspaceId}/knowledge-base/documents/${id}/reindex`, {
        method: "POST",
      });
      setFeedback({ text: "Kaynak yeniden indekslendi.", type: "success" });
      await loadOverview(search, false);
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setBusyDocId(null);
    }
  }

  async function handleDeleteSingle(id: string) {
    if (busyDocId) return;
    setBusyDocId(id);
    setFeedback(null);
    try {
      await api<void>(`/workspaces/${workspaceId}/knowledge-base/documents/${id}`, {
        method: "DELETE",
      });
      setSelectedIds((prev) => prev.filter((item) => item !== id));
      setFeedback({ text: "Kaynak silindi.", type: "success" });
      await loadOverview(search, false);
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setBusyDocId(null);
    }
  }

  async function handleBulkDelete() {
    if (bulkDeleting || selectedIds.length === 0) return;
    setBulkDeleting(true);
    setFeedback(null);
    try {
      await api(`/workspaces/${workspaceId}/knowledge-base/documents/bulk-delete`, {
        method: "POST",
        body: JSON.stringify({ documentIds: selectedIds }),
      });
      setSelectedIds([]);
      setFeedback({ text: "Seçili kaynaklar silindi.", type: "success" });
      await loadOverview(search, false);
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ text: errorMessage(err), type: "error" });
    } finally {
      setBulkDeleting(false);
    }
  }

  const documents = data?.documents ?? [];
  const docCount = data?.documentCount ?? 0;
  const maxDocCount = Math.max(1, data?.maxDocumentCount ?? 10);
  const urlCount = data?.urlCount ?? 0;
  const maxUrlCount = Math.max(1, data?.maxUrlSourceCount ?? 1);
  const usedBytes = data?.usedStorageBytes ?? 0;
  const limitBytes = Math.max(1, data?.storageLimitBytes ?? 104857600);
  const urlLimitReached = urlCount >= (data?.maxUrlSourceCount ?? 1);

  const allSelected =
    documents.length > 0 && documents.every((doc) => selectedIds.includes(doc.id));

  function toggleSelectAll(checked: boolean) {
    if (checked) {
      setSelectedIds(documents.map((doc) => doc.id));
    } else {
      setSelectedIds([]);
    }
  }

  function toggleSelectOne(id: string, checked: boolean) {
    setSelectedIds((prev) =>
      checked ? (prev.includes(id) ? prev : [...prev, id]) : prev.filter((item) => item !== id)
    );
  }

  if (loading && !data) {
    return (
      <div className="p-6 max-w-7xl mx-auto">
        <Card>
          <CardContent className="p-10 text-center text-sm text-slate-500">
            Bilgi bankası verileri yükleniyor…
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-7xl mx-auto space-y-6">
      {feedback && (
        <Alert
          variant={feedback.type === "error" ? "destructive" : "default"}
          className={
            feedback.type === "error"
              ? "border-rose-200 bg-rose-50 text-rose-700"
              : "border-emerald-200 bg-emerald-50 text-emerald-800"
          }
        >
          <AlertDescription className="flex items-center gap-2 font-medium">
            {feedback.type === "error" ? (
              <AlertCircle className="h-4 w-4 shrink-0" />
            ) : (
              <CheckCircle2 className="h-4 w-4 shrink-0" />
            )}
            <span>{feedback.text}</span>
          </AlertDescription>
        </Alert>
      )}

      {/* 1. Üst Kapasite ve Kota Kartları */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card>
          <CardHeader className="pb-2">
            <div className="flex items-center justify-between">
              <CardDescription className="font-medium text-slate-600">
                Döküman Kapasitesi
              </CardDescription>
              <Database className="h-4 w-4 text-[#7c3aed]" />
            </div>
            <CardTitle className="text-xl font-bold text-slate-900">
              {docCount} / {data?.maxDocumentCount ?? 10} Döküman
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <Progress value={Math.min(100, Math.round((docCount / maxDocCount) * 100))} />
            <p className="text-xs text-slate-500">
              PDF, DOCX, TXT, MD ve metin parçacıkları
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="pb-2">
            <div className="flex items-center justify-between">
              <CardDescription className="font-medium text-slate-600">
                Web Link Kaynağı
              </CardDescription>
              <div className="flex items-center gap-2">
                {urlLimitReached && (
                  <Badge variant="destructive" className="text-[10px]">
                    Limit Doldu
                  </Badge>
                )}
                <Globe className="h-4 w-4 text-[#7c3aed]" />
              </div>
            </div>
            <CardTitle className="text-xl font-bold text-slate-900">
              {urlCount} / {data?.maxUrlSourceCount ?? 1} Link
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <Progress value={Math.min(100, Math.round((urlCount / maxUrlCount) * 100))} />
            <p className="text-xs text-slate-500">
              Otomatik taranan HTTPS web sayfaları
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="pb-2">
            <div className="flex items-center justify-between">
              <CardDescription className="font-medium text-slate-600">
                Depolama Alanı
              </CardDescription>
              <HardDrive className="h-4 w-4 text-[#7c3aed]" />
            </div>
            <CardTitle className="text-xl font-bold text-slate-900">
              {formatBytes(usedBytes)} / {formatBytes(limitBytes)}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <Progress value={Math.min(100, Math.round((usedBytes / limitBytes) * 100))} />
            <p className="text-xs text-slate-500">
              Maksimum tekil dosya: {formatBytes(data?.maxFileSizeBytes ?? 10485760)}
            </p>
          </CardContent>
        </Card>
      </div>

      {/* 2. AI Davranış ve Kural Parametreleri (2x2 Grid — 4 Akıllı Kart) */}
      <div className="space-y-4">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
          <div>
            <h2 className="text-lg font-bold text-slate-900 flex items-center gap-2">
              <Sparkles className="h-5 w-5 text-[#7c3aed]" />
              <span>AI Davranış ve Kural Parametreleri</span>
            </h2>
            <p className="text-xs text-slate-500">
              Asistanınızın karakterini, sık sorulan soruları ve güvenlik sınırlarını belirleyin.
            </p>
          </div>
          <Button
            type="button"
            disabled={savingRules}
            onClick={() => void handleSaveRules()}
            className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
          >
            {savingRules
              ? "Kaydediliyor…"
              : "Kuralları Kaydet ve Asistanı Güncelle"}
          </Button>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm font-semibold flex items-center gap-2">
                <BookOpen className="h-4 w-4 text-[#7c3aed]" />
                <span>İş Özeti (Business Summary)</span>
              </CardTitle>
              <CardDescription className="text-xs">
                Şirketin ne yaptığı, marka kimliği ve hitap tonu.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={4}
                placeholder="Örn: Daribase e-ticaret ve SaaS firmalarına yapay zeka destekli müşteri asistanı sunar. Samimi, kurumsal ve çözüm odaklı bir dille yanıt ver."
                value={businessSummary}
                onChange={(e) => setBusinessSummary(e.target.value)}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm font-semibold flex items-center gap-2">
                <Sparkles className="h-4 w-4 text-[#7c3aed]" />
                <span>Önemli Soru Başlıkları (Key FAQ)</span>
              </CardTitle>
              <CardDescription className="text-xs">
                Sık sorulan sorular ve standart yanıtlar.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={4}
                placeholder="Örn: İade süresi kaç gün? -> Teslimattan itibaren 14 gün içinde ücretsiz iade yapılabilir."
                value={keyFaq}
                onChange={(e) => setKeyFaq(e.target.value)}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm font-semibold flex items-center gap-2">
                <ShieldAlert className="h-4 w-4 text-amber-600" />
                <span>Cevap Sınırları (Answer Boundaries)</span>
              </CardTitle>
              <CardDescription className="text-xs">
                AI&apos;ın asla söylememesi veya uydurmaması gerekenler.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={4}
                placeholder="Örn: Bilgi bankasında yer almayan fiyat veya indirim oranı verme, rakip markalar hakkında yorum yapma."
                value={answerBoundaries}
                onChange={(e) => setAnswerBoundaries(e.target.value)}
              />
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm font-semibold flex items-center gap-2">
                <Users className="h-4 w-4 text-emerald-600" />
                <span>İnsana Devir Kuralları (Escalation Rules)</span>
              </CardTitle>
              <CardDescription className="text-xs">
                Hangi durumlarda canlı müşteri temsilcisine aktaracağı.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={4}
                placeholder="Örn: Müşteri iptal/iade uyuşmazlığı yaşadığında veya canlı temsilci talep ettiğinde görüşmeyi ekibe devret."
                value={escalationRules}
                onChange={(e) => setEscalationRules(e.target.value)}
              />
            </CardContent>
          </Card>
        </div>
      </div>

      {/* 3. Döküman ve Veri Kaynağı Yükleme (Tabs) */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base font-bold text-slate-900">
            Yeni Bilgi Kaynağı Ekle
          </CardTitle>
          <CardDescription>
            Yüklediğiniz dosya, metin ve web sayfaları otomatik olarak parçalanıp vektör indeksine eklenir.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Tabs defaultValue="file">
            <TabsList className="grid w-full max-w-md grid-cols-3">
              <TabsTrigger value="file">Dosya Yükle</TabsTrigger>
              <TabsTrigger value="text">Metin Ekle</TabsTrigger>
              <TabsTrigger value="url">Web Linki Ekle</TabsTrigger>
            </TabsList>

            {/* Dosya Yükle */}
            <TabsContent value="file" className="pt-4">
              <input
                ref={fileInputRef}
                type="file"
                accept=".pdf,.docx,.txt,.md,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,text/plain,text/markdown"
                onChange={(e) => void handleFileChange(e)}
                className="hidden"
              />
              <div
                onClick={() => !uploadingFile && fileInputRef.current?.click()}
                className="cursor-pointer rounded-2xl border-2 border-dashed border-slate-200 bg-slate-50/60 hover:bg-violet-50/40 hover:border-violet-300 transition-all p-8 text-center space-y-3"
              >
                <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-2xl bg-violet-100 text-[#7c3aed]">
                  <UploadCloud className="h-6 w-6" />
                </div>
                <div className="space-y-1">
                  <p className="text-sm font-semibold text-slate-900">
                    {uploadingFile
                      ? "Dosya yükleniyor ve indeksleniyor…"
                      : "Bilgisayarınızdan dosya seçmek için tıklayın"}
                  </p>
                  <p className="text-xs text-slate-500">
                    Desteklenen formatlar: PDF, DOCX, TXT, MD (Maks.{" "}
                    {formatBytes(data?.maxFileSizeBytes ?? 10485760)})
                  </p>
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={uploadingFile}
                  onClick={(e) => {
                    e.stopPropagation();
                    fileInputRef.current?.click();
                  }}
                >
                  Dosya Seç
                </Button>
              </div>
            </TabsContent>

            {/* Metin Ekle */}
            <TabsContent value="text" className="pt-4">
              <form className="space-y-4" onSubmit={(e) => void handleAddText(e)}>
                <div className="space-y-1.5">
                  <Label htmlFor="kb-text-title">Başlık</Label>
                  <Input
                    id="kb-text-title"
                    placeholder="Örn: Kargo ve Teslimat Politikası"
                    value={textTitle}
                    onChange={(e) => setTextTitle(e.target.value)}
                    required
                  />
                </div>
                <div className="space-y-1.5">
                  <Label htmlFor="kb-text-content">İçerik</Label>
                  <Textarea
                    id="kb-text-content"
                    rows={5}
                    placeholder="Asistanın bilmesini istediğiniz detaylı metni buraya yapıştırın…"
                    value={textContent}
                    onChange={(e) => setTextContent(e.target.value)}
                    required
                  />
                </div>
                <Button
                  type="submit"
                  disabled={addingText}
                  className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
                >
                  {addingText ? "Ekleniyor…" : "Metin Kaynağını Ekle"}
                </Button>
              </form>
            </TabsContent>

            {/* Web Linki Ekle */}
            <TabsContent value="url" className="pt-4 space-y-4">
              {urlLimitReached && (
                <Alert variant="destructive">
                  <AlertDescription className="text-xs font-medium">
                    Paketinizin maksimum web linki ({data?.maxUrlSourceCount ?? 1} adet) limitine ulaştınız. Yeni link eklemek için mevcut link kaynağını silebilir veya paketinizi yükseltebilirsiniz.
                  </AlertDescription>
                </Alert>
              )}
              <form className="space-y-4" onSubmit={(e) => void handleAddUrl(e)}>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div className="space-y-1.5">
                    <Label htmlFor="kb-url-title">Başlık (İsteğe bağlı)</Label>
                    <Input
                      id="kb-url-title"
                      placeholder="Örn: Hakkımızda Sayfası"
                      disabled={urlLimitReached || addingUrl}
                      value={urlTitle}
                      onChange={(e) => setUrlTitle(e.target.value)}
                    />
                  </div>
                  <div className="space-y-1.5">
                    <Label htmlFor="kb-url-input">Web Adresi (HTTPS)</Label>
                    <Input
                      id="kb-url-input"
                      type="url"
                      placeholder="https://ornek.com/sss"
                      disabled={urlLimitReached || addingUrl}
                      value={urlValue}
                      onChange={(e) => setUrlValue(e.target.value)}
                      required
                    />
                  </div>
                </div>
                <Button
                  type="submit"
                  disabled={urlLimitReached || addingUrl}
                  className="bg-[#7c3aed] hover:bg-[#6d28d9] text-white"
                >
                  <Link2 className="h-4 w-4" />
                  <span>{addingUrl ? "Taranıyor…" : "Web Linkini Ekle"}</span>
                </Button>
              </form>
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>

      {/* 4. Kütüphane Tablosu */}
      <Card>
        <CardHeader className="pb-4">
          <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3">
            <div>
              <CardTitle className="text-base font-bold text-slate-900">
                Bilgi Bankası Kütüphanesi
              </CardTitle>
              <CardDescription>
                Toplam {documents.length} kayıt listeleniyor
              </CardDescription>
            </div>
            <div className="flex items-center gap-2">
              <div className="relative w-full sm:w-64">
                <Search className="h-4 w-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
                <Input
                  placeholder="Kaynaklarda ara…"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  className="pl-9 h-9"
                />
              </div>
              <Button
                type="button"
                variant="destructive"
                size="sm"
                disabled={selectedIds.length === 0 || bulkDeleting}
                onClick={() => void handleBulkDelete()}
              >
                <Trash2 className="h-3.5 w-3.5" />
                <span>
                  Seçilenleri Sil {selectedIds.length > 0 ? `(${selectedIds.length})` : ""}
                </span>
              </Button>
            </div>
          </div>
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-10">
                  <Checkbox
                    checked={allSelected}
                    onCheckedChange={(checked) => toggleSelectAll(checked)}
                    aria-label="Tümünü seç"
                  />
                </TableHead>
                <TableHead>Başlık / Kaynak</TableHead>
                <TableHead>Tür</TableHead>
                <TableHead>Boyut</TableHead>
                <TableHead>Tarih</TableHead>
                <TableHead>Durum</TableHead>
                <TableHead className="text-right">İşlemler</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {documents.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={7} className="h-28 text-center text-sm text-slate-500">
                    Henüz bilgi bankasına eklenmiş bir döküman veya link bulunmuyor.
                  </TableCell>
                </TableRow>
              ) : (
                documents.map((doc) => {
                  const isSelected = selectedIds.includes(doc.id);
                  const isBusy = busyDocId === doc.id;
                  return (
                    <TableRow key={doc.id} data-state={isSelected ? "selected" : undefined}>
                      <TableCell>
                        <Checkbox
                          checked={isSelected}
                          onCheckedChange={(checked) => toggleSelectOne(doc.id, checked)}
                          aria-label={doc.title}
                        />
                      </TableCell>
                      <TableCell>
                        <div className="flex items-center gap-2.5">
                          {doc.type === "URL" ? (
                            <Globe className="h-4 w-4 text-violet-600 shrink-0" />
                          ) : (
                            <FileText className="h-4 w-4 text-slate-500 shrink-0" />
                          )}
                          <div className="min-w-0">
                            <div className="font-medium text-slate-900 truncate max-w-xs">
                              {doc.title}
                            </div>
                            {doc.sourceUrl && (
                              <a
                                href={doc.sourceUrl}
                                target="_blank"
                                rel="noreferrer"
                                className="text-xs text-violet-600 hover:underline truncate block max-w-xs"
                              >
                                {doc.sourceUrl}
                              </a>
                            )}
                          </div>
                        </div>
                      </TableCell>
                      <TableCell>
                        <Badge variant="outline" className="text-[11px]">
                          {doc.type}
                        </Badge>
                      </TableCell>
                      <TableCell className="text-xs text-slate-600">
                        {formatBytes(doc.sizeBytes)}
                      </TableCell>
                      <TableCell className="text-xs text-slate-500">
                        {formatDate(doc.createdAt)}
                      </TableCell>
                      <TableCell>
                        {doc.status === "READY" ? (
                          <Badge className="bg-emerald-50 text-emerald-700 border-emerald-200 hover:bg-emerald-50">
                            İndekslendi
                          </Badge>
                        ) : doc.status === "FAILED" ? (
                          <Badge variant="destructive">Hata</Badge>
                        ) : (
                          <Badge className="bg-amber-50 text-amber-700 border-amber-200 hover:bg-amber-50">
                            Bekliyor
                          </Badge>
                        )}
                      </TableCell>
                      <TableCell className="text-right">
                        <div className="inline-flex items-center gap-1">
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={isBusy}
                            onClick={() => void handleReindex(doc.id)}
                            title="Yeniden İndeksle"
                          >
                            <RefreshCw
                              className={`h-3.5 w-3.5 ${isBusy ? "animate-spin" : ""}`}
                            />
                            <span className="hidden lg:inline">Yeniden İndeksle</span>
                          </Button>
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={isBusy}
                            onClick={() => void handleDeleteSingle(doc.id)}
                            className="text-rose-600 hover:text-rose-700 hover:bg-rose-50"
                            title="Sil"
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                            <span className="hidden lg:inline">Sil</span>
                          </Button>
                        </div>
                      </TableCell>
                    </TableRow>
                  );
                })
              )}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}
