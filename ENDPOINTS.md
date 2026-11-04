# MAIC API — Endpoint Rehberi (Daribase FAZ 1 & FAZ 2)

Bu belge, MAIC API'sini Frontend, Postman veya Bruno ile kullanmak için her endpoint'in **beklediği** ve **döndürdüğü**
veriyi özetler. Kaynak: `backend/src/MAIC.API` altındaki kod.

- Mimari: E-posta/Şifre + Yerel Cookie Oturumu, Tek Workspace / Tek AI Ajanı / Tek Bilgi Havuzu (Single Knowledge Pool), Dinamik RBAC & Paket Kotaları
- Ana Bilgi Bankası Ucu: `/workspaces/{workspaceId}/knowledge-base` (`KnowledgeBaseEndpoints`)

---

## 1. Başlamadan Önce

### 1.1 API'yi Çalıştırma

| Ayar | Değer / Not |
| --- | --- |
| Base URL | `https://localhost:58542` (`launchSettings.json`, profil `MAIC.API`). `http://localhost:58543` HTTPS'e yönlendirir. |
| Kimlik Doğrulama | Yerel E-posta/Şifre + HTTP-only Cookie (`maic-session` / `__Host-maic-session`). Harici OIDC bağımlılığı kaldırılmıştır. |
| `ConnectionStrings__Maic` | Zorunlu (PostgreSQL + `vector` eklentisi). Migration'lar: `dotnet ef database update --project backend/src/MAIC.Infrastructure --startup-project backend/src/MAIC.API`. |
| `KnowledgeStorage__RootPath` | Dosya yükleme için depolama klasörü. |
| `GEMINI_API_KEY` | Google Gemini OpenAI-uyumlu (`https://generativelanguage.googleapis.com/v1beta/openai/`) Chat (`gemini-2.5-flash` / `gemini-2.5-pro`) ve Embedding (`text-embedding-004`) çağrıları için API anahtarı. |

---

## 2. Kimlik Doğrulama ve Oturum (`/auth/*`)

| Method | Endpoint | Yetki / CSRF | Beklenen | Dönen | Hatalar |
| --- | --- | --- | --- | --- | --- |
| GET | `/health` | Anonim | — | 200 `Healthy` | — |
| GET | `/auth/csrf` | Anonim veya oturumlu | — | 200 `{ token }` + CSRF çerezi | — |
| POST | `/auth/register` | Anonim | `{ fullName, workspaceName?, email, password }` (şifre ≥ 8 karakter) | 201 `AuthSession` + oturum çerezi (ilk kullanıcı otomatik `isSuperAdmin: true` olur; tek Workspace, tek `AiAgent`, tek `KnowledgeSource` ve `Web Widget` kanalı otomatik oluşturulur) | 400, 409 (e-posta kayıtlı) |
| POST | `/auth/login` | Anonim | `{ email, password }` | 200 `AuthSession` + oturum çerezi | 401 |
| GET | `/auth/me` | Oturum | — | 200 `AuthSession` (kullanıcının workspace'i yoksa otomatik varsayılan Workspace + AiAgent + Web Widget Channel oluşturulup bağlanır) | 401 |
| POST | `/auth/logout` | Oturum, **CSRF** | — | 204, oturum çerezi silinir | 400 CSRF, 401 |

### `GET /auth/me`, `POST /auth/login` ve `POST /auth/register` Yanıt Şeması (`AuthSession`)

```json
{
  "userId": "0199a1b2-7c3d-7e4f-8a9b-0c1d2e3f4a5b",
  "email": "ornek@sirket.com",
  "fullName": "Ahmet Yılmaz",
  "isSuperAdmin": true,
  "workspace": {
    "id": "0199a1b2-...",
    "name": "Ahmet Yılmaz Çalışma Alanı",
    "role": "OWNER",
    "plan": "STARTER",
    "monthlyMessageCap": 5000,
    "usedMessagesThisMonth": 0,
    "storageLimitBytes": 104857600,
    "usedStorageBytes": 0
  },
  "permissions": [
    "billing.manage",
    "channels.manage",
    "inbox.reply",
    "inbox.view",
    "knowledge.manage",
    "settings.manage",
    "team.manage",
    "widget.manage"
  ],
  "planFeatures": [
    "widget.basic",
    "widget.branding",
    "knowledge.files",
    "knowledge.urls",
    "ai.reply",
    "inbox.handoff",
    "team.invite"
  ]
}
```

---

## 3. SuperAdmin Paket ve Kota Yönetimi (`/superadmin/plans`)

| Method | Endpoint | Yetki | Beklenen | Dönen | Hatalar |
| --- | --- | --- | --- | --- | --- |
| GET | `/superadmin/plans` | `isSuperAdmin: true` | — | 200 `PlanDefinition[]` (`FREE`, `STARTER`, `PRO`, `SCALE`) | 401, 403 |
| PUT | `/superadmin/plans/{plan}` | `isSuperAdmin: true` | `{ name?, monthlyMessageLimit, storageLimitBytes, maxFileSizeBytes, maxDocumentCount, maxUrlSourceCount, maxSeats, allowedFeaturesJson? \| allowedFeatures? }` | 200 güncellenen paket tanımı | 400, 401, 403 |

---

## 4. Birleşik Bilgi Bankası ve 4 Akıllı Kural Kartı (`/workspaces/{workspaceId}/knowledge-base`)

> **Önemli:** Frontend'de ana bilgi bankası yönetim ucu olarak `/workspaces/{workspaceId}/knowledge-base` kullanılır. Her çalışma alanının arka planda otomatik yönetilen tek bir `"Ana Bilgi Bankası"` havuzu vardır ve dosya/metin/URL eklendiğinde **Zero-Click Ingestion** (otomatik parçalama + indeksleme) anında tetiklenir.

| Method | Endpoint | Yetki | Beklenen | Dönen | Hatalar |
| --- | --- | --- | --- | --- | --- |
| GET | `/workspaces/{workspaceId}/knowledge-base?search=` | Üye (`inbox.view` / `knowledge.manage`) | İsteğe bağlı `search` filtresi | 200 `{ quotas, rules, items }` (tüm sayaçlar, 4 akıllı kural kartı ve döküman/metin/URL listesi) | 401, 403, 404 |
| GET | `/workspaces/{workspaceId}/knowledge-base/rules` | Üye | — | 200 `{ businessSummary, keyFaq, answerBoundaries, escalationRules, agentName, language, modelTier, enabled, compiledInstructions }` | 401, 403, 404 |
| PUT | `/workspaces/{workspaceId}/knowledge-base/rules` | `knowledge.manage` | `{ businessSummary, keyFaq, answerBoundaries, escalationRules, language?, modelTier?, agentName?, enabled? }` | 200 güncellenen kurallar (Workspace'in tek `AiAgent.Instructions` alanına yapılandırılmış sistem promptu olarak otomatik senkronize edilir) | 400, 401, 403, 404 |
| POST | `/workspaces/{workspaceId}/knowledge-base/files` | `knowledge.manage` | `multipart/form-data` (`file`) veya binary gövde (`.pdf`, `.docx`, `.txt`, `.md`) | 201 eklenen döküman + otomatik indeksleme | 400, 401, 403, 409 (`FILE_TOO_LARGE`, `DOCUMENT_LIMIT_EXCEEDED`, `STORAGE_LIMIT_EXCEEDED`) |
| POST | `/workspaces/{workspaceId}/knowledge-base/texts` | `knowledge.manage` | `{ title, content }` | 201 eklenen metin dökümanı + otomatik indeksleme | 400, 401, 403, 409 (`DOCUMENT_LIMIT_EXCEEDED`, `STORAGE_LIMIT_EXCEEDED`) |
| POST | `/workspaces/{workspaceId}/knowledge-base/urls` | `knowledge.manage` | `{ title, url }` (`https://` zorunlu, SSRF korumalı) | 201 çekilen web sayfası dökümanı + otomatik indeksleme | 400, 401, 403, 409 (`URL_LIMIT_EXCEEDED`, `STORAGE_LIMIT_EXCEEDED`), 422 (`URL_FETCH_FAILED`) |
| DELETE | `/workspaces/{workspaceId}/knowledge-base/documents/{id}` | `knowledge.manage` | — | 200 `{ deletedId, usedStorageBytes }` (`UsedStorageBytes` düşürülür) | 401, 403, 404 |
| POST | `/workspaces/{workspaceId}/knowledge-base/documents/bulk-delete` | `knowledge.manage` | `{ documentIds: Guid[] }` | 200 `{ deletedCount, usedStorageBytes }` | 400, 401, 403 |
| POST | `/workspaces/{workspaceId}/knowledge-base/documents/{id}/reindex` | `knowledge.manage` | — | 200 yeniden indekslenen döküman | 401, 403, 404 |

---

## 5. Workspace'ler (`/workspaces`) — Tekil Workspace Kuralı

| Method | Endpoint | Yetki / CSRF | Beklenen | Dönen | Hatalar |
| --- | --- | --- | --- | --- | --- |
| POST | `/workspaces` | Oturum, **CSRF** | `{ name }` | **Kilitli (Tek Workspace Kuralı):** Kullanıcının zaten bir workspace'i varsa `409 Conflict` (`SINGLE_WORKSPACE_LIMIT_REACHED`) döner | 400, 401, 409 |
| GET | `/workspaces?skip=0&take=50` | Oturum | `skip` ≥ 0, `take` 1–100 | 200 dizi: üye olunan workspace | 400, 401 |
| GET | `/workspaces/{id}` | Üye | — | 200 workspace | 401, 404 |
| PUT | `/workspaces/{id}` | OWNER/ADMIN, **CSRF** | `{ name }` | 200 workspace | 400, 401, 403, 404 |

---

## 6. AI Ajanı (`/workspaces/{workspaceId}/ai-agents`) — Tekil Ajan Kuralı

| Method | Endpoint | Yetki / CSRF | Beklenen | Dönen | Hatalar |
| --- | --- | --- | --- | --- | --- |
| POST | `/` | OWNER/ADMIN, **CSRF** | Tam gövde | **Kilitli (Tek Ajan Kuralı):** Workspace'te zaten 1 ajan bulunduğundan `409 Conflict` (`SINGLE_AGENT_LIMIT_REACHED`) döner | 400, 401, 403, 404, 409 |
| GET | `/?after=&take=50` | Üye | — | 200 `{ items, nextAfter }` | 400, 401, 404 |
| GET | `/{agentId}` | Üye | — | 200 detay | 401, 404 |
| PUT | `/{agentId}` | OWNER/ADMIN, **CSRF** | Tam gövde | 200 detay | 400, 401, 403, 404 |
| POST | `/{agentId}/retrieval-preview` | Üye, **CSRF** | `{ query }` | 200 `{ outcome, reason, coverage, passages }` | 400, 401, 404 |

---

## 7. Üyelik, Kişiler, Kanallar, Konuşmalar ve Widget

- **Üyelik ve Davetler (`/workspaces/{workspaceId}/members` & `/invitations`)**:
  - `GET /members`, `PUT /members/{userId}/role`, `DELETE /members/{userId}`, `POST /leave`
  - `GET /invitations`, `POST /invitations` (kullanıcı başka bir workspace'e üyeyse `409 Conflict` döner), `DELETE /invitations/{id}`, `POST /invitations/{id}/accept`
- **Kişiler (`/workspaces/{workspaceId}/contacts`)**:
  - `GET /`, `POST /`, `GET /{contactId}`, `PUT /{contactId}`
- **Kanallar (`/workspaces/{workspaceId}/channels` & `/public/channels/{publishableToken}`)**:
  - `GET /`, `POST /`, `GET /{channelId}`, `PUT /{channelId}` (`name`, `enabled`, `defaultAiAgentId`)
- **Konuşmalar (`/workspaces/{workspaceId}/conversations`)**:
  - `GET /`, `POST /`, `GET /{conversationId}`, `POST /{conversationId}/messages`, `GET /{conversationId}/messages`
  - `PUT /{conversationId}/assignment`, `POST /{conversationId}/handoffs`, `POST /{conversationId}/handoffs/{handoffId}/accept`
  - `POST /{conversationId}/resolve`, `POST /{conversationId}/reopen`, `POST /{conversationId}/ai-executions`
