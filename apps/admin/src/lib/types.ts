export type WorkspaceRole = "OWNER" | "ADMIN" | "AGENT";
export type PlanTier = "FREE" | "STARTER" | "PRO" | "SCALE";

export type Section =
  | "overview"       // 1. Genel Bakış
  | "conversations"  // 2. Sohbetler
  | "channels"       // 3. Kanallar
  | "members"        // 4. Ekip
  | "knowledge"      // 5. Bilgi Bankası
  | "billing"        // 6. Faturalama
  | "settings"       // 7. Ayarlar (Sihirbaz & Playground)
  | "widget"         // 8. Widget
  | "superadmin";    // 9. SuperAdmin (Sadece SuperAdmin yetkili kullanıcılara görünür)

export interface AuthWorkspaceSummary {
  id: string;
  name: string;
  role: WorkspaceRole;
  plan: PlanTier;
  monthlyMessageCap: number;
  usedMessagesThisMonth: number;
  storageLimitBytes: number;
  usedStorageBytes?: number;
  primaryColor?: string;
  widgetTitle?: string;
  logoUrl?: string | null;
  usedPlaygroundMessagesThisMonth?: number;
  playgroundMonthlyCap?: number;
}

export interface AuthSession {
  userId: string;
  email: string;
  fullName: string;
  isSuperAdmin: boolean;
  workspace: AuthWorkspaceSummary;
  permissions: string[];
  planFeatures: string[];
}

export interface PanelProps {
  workspaceId: string;
  userId: string;
  role: WorkspaceRole;
  session: AuthSession;
  onSessionRefresh?: () => void;
}

// --- FAZ 2: Bilgi Bankası Tipleri ---
export type KnowledgeItemType = "FILE" | "TEXT" | "URL";
export type KnowledgeItemStatus = "PENDING" | "PROCESSING" | "READY" | "FAILED";

export interface KnowledgeAiRules {
  businessSummary: string | null;
  keyFaq: string | null;
  answerBoundaries: string | null;
  escalationRules: string | null;
}

export interface KnowledgeDocumentItem {
  id: string;
  title: string;
  type: KnowledgeItemType;
  fileName?: string | null;
  sourceUrl?: string | null;
  sizeBytes: number;
  chunkCount: number;
  characterCount: number;
  status: KnowledgeItemStatus;
  failureReason?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface KnowledgeBaseOverview {
  workspaceId: string;
  defaultSourceId: string;
  documentCount: number;
  maxDocumentCount: number;
  urlCount: number;
  maxUrlSourceCount: number;
  usedStorageBytes: number;
  storageLimitBytes: number;
  maxFileSizeBytes: number;
  rules: KnowledgeAiRules;
  documents: KnowledgeDocumentItem[];
}

// --- FAZ 3: Ayarlar Sihirbazı ve Playground Tipleri ---
export interface WizardCatalogOption {
  id: string;
  category: "USE_CASE" | "ASSISTANT_ROLE" | "PRIMARY_CHANNEL" | "KNOWLEDGE_METHOD";
  key: string;
  title: string;
  description: string;
  iconName: string;
  defaultWelcomeMessage?: string | null;
  defaultBusinessSummary?: string | null;
  defaultKeyFaq?: string | null;
}

export interface WorkspaceReadinessSummary {
  profileScore: number;
  faqSeedCount: number;
  sourceCount: number;
  strengths: string[];
  missingPoints: string[];
  recommendedNextStep?: string;
}

export interface WorkspaceSettingsOverview {
  workspaceId: string;
  plan: PlanTier;
  isTrained: boolean;
  canRemoveBadge: boolean;
  brandName: string;
  widgetTitle: string;
  widgetSubtitle: string;
  primaryColor: string;
  logoUrl: string | null;
  showBadge: boolean;
  useCase: string;
  assistantRole: string;
  primaryChannel: string;
  initialKnowledgeMethod: string;
  businessSummary: string;
  welcomeMessage: string;
  keyFaq: string;
  answerBoundaries: string;
  escalationRules: string;
  aiScheduleMode: string;
  soundNotificationEnabled: boolean;
  soundTone: string;
  allowedDomains: string;
  directShareActive: boolean;
  directShareKey: string;
  publishableToken: string;
  readiness: WorkspaceReadinessSummary;
  usedPlaygroundMessagesThisMonth: number;
  playgroundMonthlyCap: number;
  catalog?: {
    useCases: WizardCatalogOption[];
    assistantRoles: WizardCatalogOption[];
    primaryChannels: WizardCatalogOption[];
    knowledgeMethods: WizardCatalogOption[];
  };
}

export interface PlaygroundChatMessage {
  role: "user" | "assistant";
  content: string;
}

export interface PlaygroundChatResponse {
  reply: string;
  citations?: { documentId: string; title: string }[];
  usedPlaygroundMessages: number;
  playgroundMonthlyCap: number;
}

export function hasPermission(session: AuthSession | null, permissionKey: string): boolean {
  if (!session) return false;
  if (session.isSuperAdmin || session.workspace.role === "OWNER") return true;
  return session.permissions.includes(permissionKey);
}

// --- FAZ 4: Widget Dağıtımı & Halka Açık Canlı Bot Tipleri ---
export interface WidgetManagementConfig {
  workspaceId: string;
  name: string;
  brandName?: string;
  widgetTitle: string;
  widgetSubtitle: string;
  primaryColor: string;
  logoUrl: string | null;
  showBadge: boolean;
  welcomeMessage: string;
  allowedDomains: string;
  directShareActive: boolean;
  directShareKey: string;
  publishableToken: string;
  embedScriptCode: string;
  directShareUrl: string;
}

export interface PublicWidgetConfig {
  workspaceId: string;
  brandName: string;
  widgetTitle: string;
  widgetSubtitle: string;
  primaryColor: string;
  logoUrl: string | null;
  showBadge: boolean;
  welcomeMessage: string;
  aiNotice: string;
  privacyNotice: string;
  suggestedQuestions: string[];
  publishableToken: string;
}

export interface PublicWidgetMessage {
  id: string;
  sequence: number;
  senderKind: "GUEST" | "AI" | "MEMBER";
  content: string;
  createdAt: string;
}

export interface PublicWidgetSessionResponse {
  sessionToken: string;
  conversationId: string;
  welcomeMessage: string;
  messages: PublicWidgetMessage[];
}

export interface PublicWidgetSendMessageResponse {
  guestMessage: PublicWidgetMessage;
  aiMessage?: PublicWidgetMessage | null;
  conversationStatus: "OPEN" | "RESOLVED";
}

// --- FAZ 5: Gelen Kutusu (Sohbetler), Handoff & Arşivleme Tipleri ---
export type ConversationStatus = "OPEN" | "RESOLVED" | "ARCHIVED";
export type ConversationMode = "BOT_ACTIVE" | "PENDING_HANDOFF" | "HUMAN_ACTIVE";
export type InboxTab =
  | "all"
  | "pending_handoff"
  | "bot_active"
  | "assigned_to_me"
  | "resolved"
  | "archived";
export type InboxSortBy = "priority" | "newest" | "oldest";
export type InboxGroupBy = "none" | "status" | "channel" | "date";

export interface InboxContactSummary {
  id: string;
  displayName: string;
  email?: string | null;
  phone?: string | null;
  notes?: string | null;
  tags: string[];
}

export interface InboxConversationItem {
  id: string;
  workspaceId: string;
  status: ConversationStatus;
  mode: ConversationMode;
  channelId: string;
  channelName: string;
  contact: InboxContactSummary;
  assignedUserId?: string | null;
  assignedUserName?: string | null;
  lastMessagePreview?: string | null;
  lastMessageSenderKind?: "GUEST" | "AI" | "MEMBER" | null;
  lastActivityAt: string;
  createdAt: string;
  resolvedAt?: string | null;
  archivedAt?: string | null;
  autoArchiveAt?: string | null;
  unreadCount: number;
}

export interface InboxCounts {
  all: number;
  pendingHandoff: number;
  botActive: number;
  assignedToMe: number;
  resolved: number;
  archived: number;
  totalUnread: number;
}

export interface InboxListResponse {
  counts: InboxCounts;
  items: InboxConversationItem[];
  groups?: {
    key: string;
    label: string;
    items: InboxConversationItem[];
  }[];
}

export interface InboxMessageItem {
  id: string;
  sequence: number;
  senderKind: "GUEST" | "AI" | "MEMBER";
  senderUserId?: string | null;
  senderName?: string | null;
  content: string;
  createdAt: string;
}

export interface InboxConversationDetail {
  conversation: InboxConversationItem;
  contact?: InboxContactSummary;
  messages: InboxMessageItem[];
  members: {
    userId: string;
    fullName: string;
    email: string;
    role: WorkspaceRole;
  }[];
}

// --- FAZ 6: Genel Bakış (Yönetim Kokpiti), Ekip, Faturalama ve SuperAdmin Tipleri ---
export interface OverviewDashboardData {
  periodLabel: string;
  dateRangeLabel: string;
  lastUpdatedLabel: string;
  readinessScore: number;
  readinessPassedCount: number;
  readinessTotalCount: number;
  unresolvedQueueCount: number;
  unassignedCount: number;
  unansweredCount: number;
  aiCoveragePercent: number;
  aiActiveConversationsText: string;
  totalConversations: number;
  activeUsers: number;
  aiSuccessRate: number;
  avgResponseSeconds: number;
  dailyInteractions: {
    dateLabel: string;
    botCount: number;
    humanCount: number;
    total: number;
  }[];
  liveFeed: {
    id: string;
    visitorName: string;
    preview: string;
    senderKind: "GUEST" | "AI" | "MEMBER";
    createdAt: string;
    relativeTime: string;
  }[];
  systemStatus: {
    documentCount: number;
    widgetLive: boolean;
    connectedChannelsCount: number;
    memberCount: number;
    maxSeats: number;
    aiMessagesUsed: number;
    monthlyMessageCap: number;
  };
  recommendations: {
    title: string;
    description: string;
    targetSection: Section;
  }[];
}

export interface TeamMemberItem {
  userId: string;
  fullName: string;
  email: string;
  initials: string;
  role: WorkspaceRole;
  isOwner: boolean;
  joinedAt: string;
}

export interface TeamOverviewResponse {
  counts: {
    totalMembers: number;
    maxSeats: number;
    adminCount: number;
    pendingInvitationsCount: number;
  };
  members: TeamMemberItem[];
}

export interface PlanDefinitionItem {
  planCode: PlanTier;
  plan?: PlanTier;
  displayName: string;
  name?: string;
  monthlyPriceTry: number;
  monthlyMessageLimit: number;
  maxStorageBytes: number;
  storageLimitBytes?: number;
  maxDocumentCount: number;
  maxUrlSourceCount: number;
  maxSeats: number;
  playgroundMonthlyLimit: number;
  allowedFeatures: string[];
}

export interface BillingOverviewResponse {
  currentPlan: PlanTier;
  usage: {
    usedMessagesThisMonth: number;
    monthlyMessageCap: number;
    usedStorageBytes: number;
    storageLimitBytes: number;
    documentCount: number;
    maxDocumentCount: number;
    urlCount: number;
    maxUrlSourceCount: number;
    memberCount: number;
    maxSeats: number;
    usedPlaygroundMessagesThisMonth: number;
    playgroundMonthlyCap: number;
  };
  plans: PlanDefinitionItem[];
}

// --- FAZ 7: Gerçek Kanal Entegrasyonları Tipleri ---
export type IntegrationProvider = "WHATSAPP" | "INSTAGRAM" | "TRENDYOL";
export type IntegrationStatus = "DISCONNECTED" | "CONNECTED" | "ERROR";

export interface ChannelIntegrationItem {
  provider: IntegrationProvider;
  status: IntegrationStatus;
  displayName?: string | null;
  externalAccountId?: string | null;
  secondaryAccountId?: string | null;
  maskedToken?: string | null;
  webhookUrl: string;
  webhookVerifyToken: string;
  verifiedMetadata?: {
    displayPhoneNumber?: string;
    verifiedName?: string;
    qualityRating?: string;
    username?: string;
  } | null;
  lastTestedAt?: string | null;
  lastWebhookReceivedAt?: string | null;
  lastErrorMessage?: string | null;
}

export interface ChannelIntegrationsOverview {
  workspaceId: string;
  items: ChannelIntegrationItem[];
}

export interface TestAndConnectResponse {
  success: boolean;
  displayName?: string;
  verifiedMetadata?: {
    displayPhoneNumber?: string;
    verifiedName?: string;
    qualityRating?: string;
    username?: string;
  };
  error?: string;
  errorCode?: string;
}




