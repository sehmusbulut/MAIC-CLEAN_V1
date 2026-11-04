"use client";

import React, { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, errorMessage } from "@/lib/api";
import type {
  PanelProps,
  TeamMemberItem,
  TeamOverviewResponse,
  WorkspaceRole,
} from "@/lib/types";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertCircle,
  AlertTriangle,
  CheckCircle2,
  Clock,
  Crown,
  Loader2,
  Send,
  Shield,
  Trash2,
  User,
  UserPlus,
  Users,
} from "lucide-react";

export function MembersPanel({ workspaceId, onSessionRefresh }: PanelProps) {
  const [data, setData] = useState<TeamOverviewResponse | null>(null);
  const [loading, setLoading] = useState(true);

  // Davet formu state'leri
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [inviteRole, setInviteRole] = useState<"AGENT" | "ADMIN">("AGENT");
  const [inviting, setInviting] = useState(false);

  // Üye işlem state'leri
  const [busyMemberId, setBusyMemberId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  useEffect(() => {
    if (!feedback) return;
    const t = setTimeout(() => setFeedback(null), 4000);
    return () => clearTimeout(t);
  }, [feedback]);

  const loadTeam = useCallback(async () => {
    setLoading(true);
    try {
      const res = await api<TeamOverviewResponse>(
        `/workspaces/${workspaceId}/team`
      );
      setData(res);
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setLoading(false);
    }
  }, [workspaceId]);

  useEffect(() => {
    void loadTeam();
  }, [loadTeam]);

  const counts = data?.counts ?? {
    totalMembers: 1,
    maxSeats: 1,
    adminCount: 1,
    pendingInvitationsCount: 0,
  };
  const members: TeamMemberItem[] = data?.members ?? [];
  const isSeatLimitReached = counts.totalMembers >= counts.maxSeats;

  const handleInviteSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!fullName.trim() || !email.trim() || inviting || isSeatLimitReached) {
      return;
    }

    setInviting(true);
    setFeedback(null);

    try {
      const updated = await api<TeamOverviewResponse>(
        `/workspaces/${workspaceId}/team/invite`,
        {
          method: "POST",
          body: JSON.stringify({
            fullName: fullName.trim(),
            email: email.trim(),
            role: inviteRole,
          }),
        }
      );
      setData(updated);
      setFullName("");
      setEmail("");
      setInviteRole("AGENT");
      setFeedback({
        type: "success",
        text: "Ekip üyesi başarıyla çalışma alanınıza eklendi.",
      });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setInviting(false);
    }
  };

  const handleRoleChange = async (
    memberUserId: string,
    nextRole: WorkspaceRole
  ) => {
    if (busyMemberId) return;
    setBusyMemberId(memberUserId);
    setFeedback(null);

    try {
      const updated = await api<TeamOverviewResponse>(
        `/workspaces/${workspaceId}/team/members/${memberUserId}/role`,
        {
          method: "PUT",
          body: JSON.stringify({ role: nextRole }),
        }
      );
      setData(updated);
      setFeedback({
        type: "success",
        text: "Ekip üyesinin rolü güncellendi.",
      });
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setBusyMemberId(null);
    }
  };

  const handleRemoveMember = async (member: TeamMemberItem) => {
    if (busyMemberId || member.isOwner) return;
    setBusyMemberId(member.userId);
    setFeedback(null);

    try {
      const updated = await api<TeamOverviewResponse>(
        `/workspaces/${workspaceId}/team/members/${member.userId}`,
        {
          method: "DELETE",
        }
      );
      setData(updated);
      setFeedback({
        type: "success",
        text: `${member.fullName} çalışma alanı ekibinden çıkarıldı.`,
      });
      onSessionRefresh?.();
    } catch (err) {
      setFeedback({ type: "error", text: errorMessage(err) });
    } finally {
      setBusyMemberId(null);
    }
  };

  if (loading && !data) {
    return (
      <div className="p-6 max-w-[1400px] mx-auto flex items-center justify-center min-h-[420px]">
        <div className="flex flex-col items-center gap-3 text-slate-500">
          <Loader2 className="w-8 h-8 animate-spin text-[#7c3aed]" />
          <p className="text-sm font-medium">Ekip üyeleri yükleniyor...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="p-6 max-w-[1400px] mx-auto space-y-6">
      {/* 1. Üst Başlık */}
      <div className="space-y-2">
        <span className="bg-orange-50 text-orange-700 border border-orange-200 text-[11px] font-semibold px-3 py-1 rounded-full inline-flex items-center gap-1.5">
          <Users className="w-3.5 h-3.5" />
          ÇALIŞMA ALANI EKİBİ
        </span>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
            Ekip Üyeleri
          </h1>
          <p className="text-sm text-slate-500 mt-0.5">
            Çalışma alanınızı ve ekip üyelerine atanan rolleri yönetin.
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

      {/* 2. Üst 3'lü Sayaç Kartı */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                TOPLAM ÜYE
              </p>
              <div className="flex items-baseline gap-2.5">
                <span className="text-3xl font-extrabold text-slate-900">
                  {counts.totalMembers}
                </span>
                <span className="text-xs font-semibold text-slate-500">
                  {counts.totalMembers} / {counts.maxSeats} Koltuk
                </span>
              </div>
              {isSeatLimitReached && (
                <Badge
                  variant="secondary"
                  className="bg-orange-50 text-orange-700 border-orange-200 text-[10px]"
                >
                  Paket koltuk limitine ulaşıldı
                </Badge>
              )}
            </div>
            <div className="w-10 h-10 rounded-xl bg-violet-50 text-[#7c3aed] flex items-center justify-center shrink-0">
              <Users className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>

        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                ADMİNLER
              </p>
              <div className="text-3xl font-extrabold text-slate-900">
                {counts.adminCount}
              </div>
              <p className="text-xs text-slate-400">
                Yönetim yetkisine sahip üyeler
              </p>
            </div>
            <div className="w-10 h-10 rounded-xl bg-amber-50 text-amber-600 flex items-center justify-center shrink-0">
              <Shield className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>

        <Card className="rounded-2xl border-slate-200/80 shadow-xs">
          <CardContent className="p-5 flex items-start justify-between gap-4">
            <div className="space-y-1.5">
              <p className="text-[11px] font-bold uppercase tracking-wider text-slate-500">
                BEKLEYEN DAVETLER
              </p>
              <div className="text-3xl font-extrabold text-slate-900">
                {counts.pendingInvitationsCount}
              </div>
              <p className="text-xs text-slate-400">
                Onay bekleyen davet bağlantıları
              </p>
            </div>
            <div className="w-10 h-10 rounded-xl bg-slate-100 text-slate-600 flex items-center justify-center shrink-0">
              <Clock className="w-5 h-5" />
            </div>
          </CardContent>
        </Card>
      </div>

      {/* 3. Rol Farkları Kartı */}
      <div className="bg-white rounded-2xl border border-slate-200 p-5 space-y-4 shadow-2xs">
        <div className="space-y-1">
          <div className="flex items-center gap-2 text-sm font-bold text-slate-900">
            <AlertTriangle className="w-4 h-4 text-amber-500" />
            <span>Rol Farkları</span>
          </div>
          <p className="text-xs text-slate-500">
            Bu çalışma alanı tek sahip modeliyle çalışır. Kritik kontrol her
            zaman tek bir owner üzerinde kalır.
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 space-y-1.5">
            <div className="flex items-center gap-2 text-xs font-bold text-slate-900">
              <Crown className="w-4 h-4 text-amber-600" />
              <span>Sahip</span>
            </div>
            <p className="text-xs text-slate-600 leading-relaxed">
              Çalışma alanının tek kritik sahibidir. Ekip ekranından silinmez
              veya rolü değiştirilmez.
            </p>
          </div>

          <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 space-y-1.5">
            <div className="flex items-center gap-2 text-xs font-bold text-slate-900">
              <Shield className="w-4 h-4 text-[#7c3aed]" />
              <span>Admin</span>
            </div>
            <p className="text-xs text-slate-600 leading-relaxed">
              Günlük operasyonları, ekip üyelerini, kanalları ve ayarları
              yönetir; fakat sahip rolüne erişemez.
            </p>
          </div>

          <div className="p-4 rounded-xl border border-slate-200/80 bg-slate-50/50 space-y-1.5">
            <div className="flex items-center gap-2 text-xs font-bold text-slate-900">
              <User className="w-4 h-4 text-slate-600" />
              <span>Agent</span>
            </div>
            <p className="text-xs text-slate-600 leading-relaxed">
              Sohbetleri yönetir. Yönetim, faturalama ve ekip izinleri tarafında
              yetkisi yoktur.
            </p>
          </div>
        </div>
      </div>

      {/* 4. Ekip Üyesi Davet Et Kartı */}
      <div className="bg-white rounded-2xl border border-slate-200 p-5 space-y-4 shadow-2xs">
        <div className="flex items-center justify-between gap-2 flex-wrap">
          <div className="flex items-center gap-2 text-sm font-bold text-slate-900">
            <UserPlus className="w-4 h-4 text-[#7c3aed]" />
            <span>Ekip Üyesi Davet Et</span>
          </div>

          {isSeatLimitReached && (
            <span className="text-xs font-medium text-amber-700 bg-amber-50 border border-amber-200 px-3 py-1 rounded-full">
              Koltuk limitiniz doldu ({counts.totalMembers}/{counts.maxSeats}).
              Yeni üye eklemek için Faturalama sekmesinden paketinizi yükseltin.
            </span>
          )}
        </div>

        <form
          onSubmit={handleInviteSubmit}
          className="grid grid-cols-1 md:grid-cols-[1fr_1fr_180px_auto] gap-3 items-end"
        >
          <div className="space-y-1.5">
            <Label className="text-xs font-semibold text-slate-700">
              Ad Soyad
            </Label>
            <Input
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              placeholder="Ad Soyad"
              disabled={isSeatLimitReached || inviting}
              className="h-10 rounded-xl"
            />
          </div>

          <div className="space-y-1.5">
            <Label className="text-xs font-semibold text-slate-700">
              E-posta adresi
            </Label>
            <Input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="ekip@company.com"
              disabled={isSeatLimitReached || inviting}
              className="h-10 rounded-xl"
            />
          </div>

          <div className="space-y-1.5">
            <Label className="text-xs font-semibold text-slate-700">Rol</Label>
            <Select
              value={inviteRole}
              onValueChange={(v) => setInviteRole(v as "AGENT" | "ADMIN")}
              disabled={isSeatLimitReached || inviting}
            >
              <SelectTrigger className="h-10 rounded-xl">
                <SelectValue>
                  {inviteRole === "ADMIN" ? "Admin" : "Agent"}
                </SelectValue>
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="AGENT">Agent</SelectItem>
                <SelectItem value="ADMIN">Admin</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <Button
            type="submit"
            disabled={
              isSeatLimitReached ||
              inviting ||
              !fullName.trim() ||
              !email.trim()
            }
            className="h-10 px-5 rounded-xl bg-[#7c3aed] hover:bg-[#6d28d9] text-white font-semibold"
          >
            {inviting ? (
              <>
                <Loader2 className="w-4 h-4 mr-1.5 animate-spin" />
                Gönderiliyor...
              </>
            ) : (
              <>
                <Send className="w-4 h-4 mr-1.5" />
                Davet Gönder
              </>
            )}
          </Button>
        </form>

        <p className="text-xs text-slate-400">
          Bu çalışma alanında tek sahip bulunur. Ekip ekranından sadece admin ve
          agent davet edebilirsiniz.
        </p>
      </div>

      {/* 5. Aktif Üyeler Listesi Kartı */}
      <div className="bg-white rounded-2xl border border-slate-200 overflow-hidden shadow-2xs">
        <div className="px-5 py-4 border-b border-slate-100 flex items-center justify-between">
          <div className="flex items-center gap-2 text-sm font-bold text-slate-900">
            <Users className="w-4 h-4 text-[#7c3aed]" />
            <span>Aktif Üyeler</span>
          </div>
          <Badge variant="secondary" className="text-xs">
            {members.length}
          </Badge>
        </div>

        <div className="divide-y divide-slate-100">
          {members.map((member) => {
            const isOwner = member.isOwner || member.role === "OWNER";
            const isBusy = busyMemberId === member.userId;

            return (
              <div
                key={member.userId}
                className="px-5 py-4 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 hover:bg-slate-50/50 transition-colors"
              >
                <div className="flex items-center gap-3 min-w-0">
                  <Avatar className="h-10 w-10 border border-slate-200">
                    <AvatarFallback className="bg-violet-50 text-[#7c3aed] text-xs font-bold">
                      {member.initials}
                    </AvatarFallback>
                  </Avatar>
                  <div className="min-w-0">
                    <p className="text-sm font-bold text-slate-900 truncate">
                      {member.fullName}
                    </p>
                    <p className="text-xs text-slate-500 truncate">
                      {member.email}
                    </p>
                  </div>
                </div>

                <div className="flex items-center gap-2.5 self-end sm:self-auto">
                  {isOwner ? (
                    <Badge
                      variant="secondary"
                      className="bg-amber-50 text-amber-800 border-amber-200 px-3 py-1 text-xs font-bold inline-flex items-center gap-1.5"
                    >
                      <Crown className="w-3.5 h-3.5 text-amber-600" />
                      SAHİP
                    </Badge>
                  ) : (
                    <>
                      <div className="w-32">
                        <Select
                          value={member.role}
                          onValueChange={(v) =>
                            void handleRoleChange(
                              member.userId,
                              v as WorkspaceRole
                            )
                          }
                          disabled={isBusy}
                        >
                          <SelectTrigger className="h-9 text-xs rounded-xl">
                            <SelectValue>
                              {member.role === "ADMIN" ? "Admin" : "Agent"}
                            </SelectValue>
                          </SelectTrigger>
                          <SelectContent>
                            <SelectItem value="AGENT">Agent</SelectItem>
                            <SelectItem value="ADMIN">Admin</SelectItem>
                          </SelectContent>
                        </Select>
                      </div>

                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        disabled={isBusy}
                        onClick={() => void handleRemoveMember(member)}
                        title="Üyeyi Ekipten Çıkar"
                        className="h-9 w-9 rounded-xl text-slate-400 hover:text-rose-600 hover:bg-rose-50"
                      >
                        {isBusy ? (
                          <Loader2 className="w-4 h-4 animate-spin" />
                        ) : (
                          <Trash2 className="w-4 h-4" />
                        )}
                      </Button>
                    </>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
