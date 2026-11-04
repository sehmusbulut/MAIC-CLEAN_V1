using MAIC.Domain.AI;
using MAIC.Domain.Identity;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Domain.Widget;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed partial class MaicDbContext(DbContextOptions<MaicDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceRolePermission> WorkspaceRolePermissions => Set<WorkspaceRolePermission>();
    public DbSet<PlatformRole> PlatformRoles => Set<PlatformRole>();
    public DbSet<PlanDefinition> PlanDefinitions => Set<PlanDefinition>();
    public DbSet<WizardCatalogItem> WizardCatalogItems => Set<WizardCatalogItem>();
    public DbSet<ChannelIntegration> ChannelIntegrations => Set<ChannelIntegration>();

    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactIdentity> ContactIdentities => Set<ContactIdentity>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<GuestSession> GuestSessions => Set<GuestSession>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ConversationAssignment> ConversationAssignments => Set<ConversationAssignment>();
    public DbSet<HandoffRequest> HandoffRequests => Set<HandoffRequest>();
    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeDocumentChunk> KnowledgeDocumentChunks => Set<KnowledgeDocumentChunk>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<AiAgent> AiAgents => Set<AiAgent>();
    public DbSet<AiAgentKnowledgeSource> AiAgentKnowledgeSources => Set<AiAgentKnowledgeSource>();
    public DbSet<AiExecution> AiExecutions => Set<AiExecution>();

    // Raw SQL literals inherit the checkout's line endings; keep the model identical to the LF migration snapshot.
    private static string Sql(string sql) => sql.ReplaceLineEndings("\n");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAiExecution(modelBuilder);
        ConfigureWidget(modelBuilder);
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations", table =>
            {
                table.HasCheckConstraint("CK_Conversations_Status", "\"Status\" IN ('OPEN', 'RESOLVED')");
                table.HasCheckConstraint("CK_Conversations_Sequence", "\"LastMessageSequence\" >= 0");
                table.HasCheckConstraint("CK_Conversations_WorkflowSequences", "\"LastAssignmentSequence\" >= 0 AND \"LastHandoffSequence\" >= 0");
                table.HasCheckConstraint("CK_Conversations_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(8).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.CreatedByUserId, x.ClientRequestId }).IsUnique();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Channel>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ChannelId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Contact>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ContactId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkspaceMember>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AssignedUserId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ConversationAssignment>(entity =>
        {
            entity.ToTable("ConversationAssignments", table =>
            {
                table.HasCheckConstraint("CK_ConversationAssignments_Sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_ConversationAssignments_Changed", "\"PreviousAssignedUserId\" IS DISTINCT FROM \"AssignedUserId\"");
                table.HasCheckConstraint("CK_ConversationAssignments_Reason", "\"Reason\" IN ('MANUAL', 'HANDOFF_ACCEPTED', 'MEMBER_REMOVED')");
                table.HasCheckConstraint("CK_ConversationAssignments_ReasonTarget", Sql("""
                    ("Reason" <> 'HANDOFF_ACCEPTED' OR ("AssignedUserId" IS NOT NULL AND "AssignedUserId" = "ActorUserId"))
                    AND ("Reason" <> 'MEMBER_REMOVED' OR "AssignedUserId" IS NULL)
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.PreviousAssignedUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<HandoffRequest>(entity =>
        {
            entity.ToTable("HandoffRequests", table =>
            {
                table.HasCheckConstraint("CK_HandoffRequests_Sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_HandoffRequests_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_HandoffRequests_Lifecycle", Sql("""
                    ("Status" = 'PENDING' AND "TerminalActorUserId" IS NULL AND "TerminalAt" IS NULL AND "TerminalReason" IS NULL)
                    OR ("TerminalActorUserId" IS NOT NULL AND "TerminalAt" IS NOT NULL AND "TerminalAt" >= "CreatedAt"
                        AND "TerminalReason" IS NOT NULL
                        AND (("Status" = 'ACCEPTED' AND "TerminalReason" = 'ACCEPTED')
                            OR ("Status" = 'CANCELLED' AND "TerminalReason" IN ('MANUAL_ASSIGNMENT', 'RESOLVED'))))
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(9).IsRequired();
            entity.Property(x => x.TerminalReason).HasConversion<string>().HasMaxLength(17);
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.RequesterUserId, x.ClientRequestId }).IsUnique();
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId }).IsUnique().HasFilter("\"Status\" = 'PENDING'");
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.TerminalActorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Message>(entity =>
        {
            entity.ToTable("Messages", table =>
            {
                table.HasCheckConstraint("CK_Messages_Sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_Messages_RequestKey", "\"ClientMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                // PostgreSQL counts code points; supplementary code points consume two UTF-16 units.
                table.HasCheckConstraint("CK_Messages_Content", Sql("""
                    length("Content") + length(regexp_replace("Content", U&'[^\+010000-\+10FFFF]', '', 'g')) <= 4000
                    AND "Content" !~ U&'[\0001-\0008\000B\000C\000E-\001F\007F-\009F]'
                    AND "Content" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Content).HasMaxLength(4000).UseCollation("C").IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.SenderUserId, x.ClientMessageId }).IsUnique();
            entity.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.Sequence }).IsUnique();
            entity.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            // User identity is durable even when the sender's workspace membership is removed.
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.SenderUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("Channels", table =>
            {
                table.HasCheckConstraint("CK_Channels_Type", "\"Type\" = 'WEB'");
                table.HasCheckConstraint("CK_Channels_Name", "length(btrim(\"Name\")) BETWEEN 1 AND 200 AND \"Name\" !~ '[[:cntrl:]]'");
                table.HasCheckConstraint("CK_Channels_PublishableToken", "\"PublishableToken\" ~ '^[A-Za-z0-9_-]{43}$'");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(3).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).UseCollation("C").IsRequired();
            entity.Property(x => x.PublishableToken).HasMaxLength(43).UseCollation("C").IsRequired();
            entity.HasIndex(x => x.PublishableToken).IsUnique();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KnowledgeSource>(entity =>
        {
            entity.ToTable("KnowledgeSources", table =>
            {
                table.HasCheckConstraint("CK_KnowledgeSources_Kind", "\"Kind\" IN ('FILE', 'TEXT', 'WEBSITE')");
                // Mirrors KnowledgeSource.IsValidName for stored (trimmed) names with explicit code-point classes, because
                // [[:cntrl:]] under collation C is ASCII-only: no C0/DEL/C1 controls, a character outside .NET's
                // whitespace set, and 1-200 UTF-16 units (supplementary code points count twice).
                table.HasCheckConstraint("CK_KnowledgeSources_Name", Sql("""
                    length("Name") + length(regexp_replace("Name", U&'[^\+010000-\+10FFFF]', '', 'g')) BETWEEN 1 AND 200
                    AND "Name" !~ U&'[\0001-\001F\007F-\009F]'
                    AND "Name" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                    """));
                // Only IS [NOT] NULL tests on the nullable payload columns, so no NULL combination evaluates to NULL.
                table.HasCheckConstraint("CK_KnowledgeSources_Payload", Sql("""
                    ("Kind" = 'FILE' AND "TextContent" IS NULL AND "WebsiteUrl" IS NULL)
                    OR ("Kind" = 'TEXT' AND "TextContent" IS NOT NULL AND "WebsiteUrl" IS NULL)
                    OR ("Kind" = 'WEBSITE' AND "TextContent" IS NULL AND "WebsiteUrl" IS NOT NULL)
                    """));
                // PostgreSQL counts code points; supplementary code points consume two UTF-16 units.
                table.HasCheckConstraint("CK_KnowledgeSources_TextContent", Sql("""
                    "TextContent" IS NULL OR (
                        length("TextContent") + length(regexp_replace("TextContent", U&'[^\+010000-\+10FFFF]', '', 'g')) <= 64000
                        AND "TextContent" !~ U&'[\0001-\0008\000B\000C\000E-\001F\007F-\009F]'
                        AND "TextContent" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]')
                    """));
                // Backstop only: a stored URL is not fetched or considered network-safe.
                table.HasCheckConstraint("CK_KnowledgeSources_WebsiteUrl", Sql("""
                    "WebsiteUrl" IS NULL OR (
                        length("WebsiteUrl") + length(regexp_replace("WebsiteUrl", U&'[^\+010000-\+10FFFF]', '', 'g')) <= 2048
                        AND "WebsiteUrl" ~ '^[Hh][Tt][Tt][Pp][Ss]?://[^/?#@]+([/?]|$)'
                        AND "WebsiteUrl" !~ U&'[\0001-\0020\007F-\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                        AND strpos("WebsiteUrl", '#') = 0
                        AND strpos("WebsiteUrl", chr(92)) = 0)
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            // Tenant-safe principal for future composite foreign keys (for example documents of a source).
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(7).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).UseCollation("C").IsRequired();
            entity.Property(x => x.TextContent).HasMaxLength(64000).UseCollation("C");
            entity.Property(x => x.WebsiteUrl).HasMaxLength(2048).UseCollation("C");
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<KnowledgeDocument>(entity =>
        {
            entity.ToTable("KnowledgeDocuments", table =>
            {
                table.HasCheckConstraint("CK_KnowledgeDocuments_Origin", "\"Origin\" IN ('UPLOAD', 'TEXT', 'WEBSITE')");
                table.HasCheckConstraint("CK_KnowledgeDocuments_Status", "\"Status\" IN ('PENDING', 'READY', 'FAILED', 'SUPERSEDED')");
                table.HasCheckConstraint("CK_KnowledgeDocuments_FailureReason", Sql("""
                    "FailureReason" IS NULL OR "FailureReason" IN ('SOURCE_DISABLED', 'UNSUPPORTED_CONTENT', 'INVALID_CONTENT',
                        'EMPTY_CONTENT', 'CONTENT_TOO_LARGE', 'FETCH_BLOCKED', 'FETCH_FAILED', 'STORAGE_MISSING',
                        'STORAGE_UNAVAILABLE', 'LEASE_EXPIRED', 'INTERNAL_ERROR')
                    """));
                table.HasCheckConstraint("CK_KnowledgeDocuments_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_KnowledgeDocuments_Counts", "\"ChunkCount\" >= 0 AND \"CharacterCount\" >= 0");
                // Only IS [NOT] NULL tests on nullable columns decide the shape, so no NULL combination passes by evaluating to NULL.
                table.HasCheckConstraint("CK_KnowledgeDocuments_Lifecycle", Sql("""
                    ("Status" = 'PENDING' AND "FailureReason" IS NULL AND "ProcessedAt" IS NULL
                        AND "ChunkCount" = 0 AND "CharacterCount" = 0)
                    OR ("Status" = 'READY' AND "FailureReason" IS NULL AND "ProcessedAt" IS NOT NULL
                        AND "ChunkCount" > 0 AND "CharacterCount" > 0 AND "ContentType" IS NOT NULL
                        AND "SizeBytes" IS NOT NULL AND "ContentSha256" IS NOT NULL)
                    OR ("Status" = 'FAILED' AND "FailureReason" IS NOT NULL AND "ProcessedAt" IS NOT NULL
                        AND "ChunkCount" = 0 AND "CharacterCount" = 0)
                    OR ("Status" = 'SUPERSEDED' AND "FailureReason" IS NULL AND "ProcessedAt" IS NOT NULL AND "Origin" <> 'UPLOAD')
                    """));
                table.HasCheckConstraint("CK_KnowledgeDocuments_OriginShape", Sql("""
                    ("Origin" = 'UPLOAD' AND "FileName" IS NOT NULL AND "SourceUrl" IS NULL AND "ContentSha256" IS NOT NULL
                        AND "ContentType" IS NOT NULL AND "ContentType" IN ('text/plain', 'text/markdown', 'application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document')
                        AND "SizeBytes" IS NOT NULL AND "SizeBytes" BETWEEN 1 AND 52428800)
                    OR ("Origin" = 'TEXT' AND "FileName" IS NULL AND "SourceUrl" IS NULL)
                    OR ("Origin" = 'WEBSITE' AND "FileName" IS NULL)
                    """));
                table.HasCheckConstraint("CK_KnowledgeDocuments_Content", Sql("""
                    ("ContentSha256" IS NULL OR "ContentSha256" ~ '^[0-9a-f]{64}$')
                    AND ("SizeBytes" IS NULL OR "SizeBytes" >= 0)
                    AND ("ContentType" IS NULL OR "ContentType" ~ '^[a-z0-9.+-]+/[a-z0-9.+-]+$')
                    AND ("SourceUrl" IS NULL OR "SourceUrl" ~ '^[Hh][Tt][Tt][Pp][Ss]?://[^/?#@]+([/?]|$)')
                    """));
                // Mirrors KnowledgeDocument.IsValidFileName: 1-255 UTF-16 units, no C0/DEL/C1 controls or path separators,
                // no surrounding whitespace and not a dot name.
                table.HasCheckConstraint("CK_KnowledgeDocuments_FileName", Sql("""
                    "FileName" IS NULL OR (
                        length("FileName") + length(regexp_replace("FileName", U&'[^\+010000-\+10FFFF]', '', 'g')) BETWEEN 1 AND 255
                        AND "FileName" !~ U&'[\0001-\001F\007F-\009F/]'
                        AND strpos("FileName", chr(92)) = 0
                        AND "FileName" !~ U&'^[\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                        AND "FileName" !~ U&'[\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]$'
                        AND "FileName" NOT IN ('.', '..'))
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.Origin).HasConversion<string>().HasMaxLength(7).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(10).IsRequired();
            entity.Property(x => x.FailureReason).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.FileName).HasMaxLength(255).UseCollation("C");
            entity.Property(x => x.ContentType).HasMaxLength(100).UseCollation("C");
            entity.Property(x => x.ContentSha256).HasMaxLength(64).IsFixedLength().UseCollation("C");
            entity.Property(x => x.SourceUrl).HasMaxLength(2048).UseCollation("C");
            entity.HasIndex(x => new { x.WorkspaceId, x.CreatedByUserId, x.ClientRequestId }).IsUnique();
            // Also the principal for vectors (WorkspaceId, KnowledgeSourceId, DocumentId): a vector's source is its document's.
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.KnowledgeSourceId, x.Id });
            // At most one pending TEXT/WEBSITE snapshot per source; concurrent requests cannot both enqueue.
            entity.HasIndex(x => x.KnowledgeSourceId).IsUnique().HasDatabaseName("IX_KnowledgeDocuments_PendingSnapshot")
                .HasFilter("\"Status\" = 'PENDING' AND \"Origin\" <> 'UPLOAD'");
            entity.HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.KnowledgeSourceId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KnowledgeDocumentChunk>(entity =>
        {
            entity.ToTable("KnowledgeDocumentChunks", table =>
            {
                table.HasCheckConstraint("CK_KnowledgeDocumentChunks_Position", "\"Sequence\" > 0 AND \"StartOffset\" >= 0");
                table.HasCheckConstraint("CK_KnowledgeDocumentChunks_Content", Sql("""
                    length("Content") + length(regexp_replace("Content", U&'[^\+010000-\+10FFFF]', '', 'g')) BETWEEN 1 AND 2000
                    AND "Content" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Content).HasMaxLength(2000).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.DocumentId, x.Sequence }).IsUnique();
            // Principal for vectors (WorkspaceId, DocumentId, ChunkId): a vector can only exist for a chunk of its document.
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.DocumentId, x.Id });
            entity.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.DocumentId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<IngestionJob>(entity =>
        {
            entity.ToTable("IngestionJobs", table =>
            {
                table.HasCheckConstraint("CK_IngestionJobs_Status", "\"Status\" IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED')");
                table.HasCheckConstraint("CK_IngestionJobs_LastError", Sql("""
                    "LastError" IS NULL OR "LastError" IN ('SOURCE_DISABLED', 'UNSUPPORTED_CONTENT', 'INVALID_CONTENT',
                        'EMPTY_CONTENT', 'CONTENT_TOO_LARGE', 'FETCH_BLOCKED', 'FETCH_FAILED', 'STORAGE_MISSING',
                        'STORAGE_UNAVAILABLE', 'LEASE_EXPIRED', 'INTERNAL_ERROR')
                    """));
                table.HasCheckConstraint("CK_IngestionJobs_Attempts", "\"MaxAttempts\" BETWEEN 1 AND 20 AND \"Attempts\" BETWEEN 0 AND \"MaxAttempts\"");
                table.HasCheckConstraint("CK_IngestionJobs_Lifecycle", Sql("""
                    ("Status" = 'QUEUED' AND "LeaseToken" IS NULL AND "CompletedAt" IS NULL AND "Attempts" < "MaxAttempts")
                    OR ("Status" = 'RUNNING' AND "LeaseToken" IS NOT NULL AND "CompletedAt" IS NULL AND "Attempts" > 0)
                    OR ("Status" = 'SUCCEEDED' AND "LeaseToken" IS NULL AND "CompletedAt" IS NOT NULL AND "LastError" IS NULL AND "Attempts" > 0)
                    OR ("Status" = 'FAILED' AND "LeaseToken" IS NULL AND "CompletedAt" IS NOT NULL AND "LastError" IS NOT NULL AND "Attempts" > 0)
                    OR ("Status" = 'CANCELLED' AND "LeaseToken" IS NULL AND "CompletedAt" IS NOT NULL AND "LastError" IS NOT NULL)
                    """));
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(9).IsRequired();
            entity.Property(x => x.LastError).HasConversion<string>().HasMaxLength(32);
            entity.Ignore(x => x.IsActive);
            entity.Ignore(x => x.CanAttempt);
            entity.HasIndex(x => new { x.WorkspaceId, x.DocumentId, x.Id });
            // One active job per document, and a small index of due work for claiming.
            entity.HasIndex(x => x.DocumentId).IsUnique().HasDatabaseName("IX_IngestionJobs_ActiveDocument")
                .HasFilter("\"Status\" IN ('QUEUED', 'RUNNING')");
            entity.HasIndex(x => new { x.NextAttemptAt, x.Id }).HasDatabaseName("IX_IngestionJobs_Due")
                .HasFilter("\"Status\" IN ('QUEUED', 'RUNNING')");
            // Live attempts per workspace for fair claiming; stays small because only RUNNING rows are indexed.
            entity.HasIndex(x => new { x.WorkspaceId, x.NextAttemptAt }).HasDatabaseName("IX_IngestionJobs_Running")
                .HasFilter("\"Status\" = 'RUNNING'");
            entity.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.DocumentId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AiAgent>(entity =>
        {
            entity.ToTable("AiAgents", table =>
            {
                table.HasCheckConstraint("CK_AiAgents_ModelTier", "\"ModelTier\" IN ('FAST', 'BALANCED', 'QUALITY')");
                table.HasCheckConstraint("CK_AiAgents_KnowledgeScope", "\"KnowledgeScope\" IN ('ALL_SOURCES', 'SELECTED_SOURCES')");
                table.HasCheckConstraint("CK_AiAgents_MaxAiReplies", "\"MaxAiReplies\" IS NULL OR \"MaxAiReplies\" BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_AiAgents_Timestamps", "\"UpdatedAt\" >= \"CreatedAt\"");
                // Same rule as knowledge source names (see CK_KnowledgeSources_Name).
                table.HasCheckConstraint("CK_AiAgents_Name", Sql("""
                    length("Name") + length(regexp_replace("Name", U&'[^\+010000-\+10FFFF]', '', 'g')) BETWEEN 1 AND 200
                    AND "Name" !~ U&'[\0001-\001F\007F-\009F]'
                    AND "Name" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]'
                    """));
                // Same rule as TEXT knowledge content, bounded to 8000 UTF-16 units.
                table.HasCheckConstraint("CK_AiAgents_Instructions", Sql("""
                    "Instructions" IS NULL OR (
                        length("Instructions") + length(regexp_replace("Instructions", U&'[^\+010000-\+10FFFF]', '', 'g')) <= 8000
                        AND "Instructions" !~ U&'[\0001-\0008\000B\000C\000E-\001F\007F-\009F]'
                        AND "Instructions" ~ U&'[^\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]')
                    """));
                table.HasCheckConstraint("CK_AiAgents_HiddenCharacters", Sql("""
                    "Name" !~ U&'[\202A-\202E\2066-\2069\200B\2060\FEFF\+0E0000-\+0E007F]'
                    AND ("Instructions" IS NULL OR "Instructions" !~ U&'[\202A-\202E\2066-\2069\200B\2060\FEFF\+0E0000-\+0E007F]')
                    """));
                table.HasCheckConstraint("CK_AiAgents_Language",
                    "\"Language\" IS NULL OR (length(\"Language\") <= 35 AND \"Language\" ~ '^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(200).UseCollation("C").IsRequired();
            entity.Property(x => x.Instructions).HasMaxLength(8000).UseCollation("C");
            entity.Property(x => x.Language).HasMaxLength(35).UseCollation("C");
            entity.Property(x => x.ModelTier).HasConversion<string>().HasMaxLength(8).IsRequired();
            entity.Property(x => x.KnowledgeScope).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.KnowledgeSources).WithOne().HasForeignKey(x => new { x.WorkspaceId, x.AiAgentId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(x => x.KnowledgeSources).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<AiAgentKnowledgeSource>(entity =>
        {
            entity.ToTable("AiAgentKnowledgeSources");
            entity.HasKey(x => new { x.AiAgentId, x.KnowledgeSourceId });
            // Tenant-safe: the selected source must belong to the agent's workspace.
            entity.HasOne<KnowledgeSource>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.KnowledgeSourceId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkspaceId, x.KnowledgeSourceId });
        });
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.ToTable("Contacts", table => table.HasCheckConstraint("CK_Contacts_DisplayName",
                "length(btrim(\"DisplayName\")) BETWEEN 1 AND 200 AND \"DisplayName\" !~ '[[:cntrl:]]'"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.Property(x => x.DisplayName).HasMaxLength(200).UseCollation("C").IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Identities).WithOne().HasForeignKey(x => new { x.WorkspaceId, x.ContactId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(x => x.Identities).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<ContactIdentity>(entity =>
        {
            entity.ToTable("ContactIdentities", table =>
            {
                table.HasCheckConstraint("CK_ContactIdentities_Kind", "\"Kind\" ~ '^[a-z][a-z0-9._:-]{0,63}$'");
                table.HasCheckConstraint("CK_ContactIdentities_Value", "length(\"Value\") BETWEEN 1 AND 512 AND \"Value\" !~ '[[:cntrl:]]'");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Kind).HasMaxLength(64).UseCollation("C").IsRequired();
            entity.Property(x => x.Value).HasMaxLength(512).UseCollation("C").IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.Kind, x.Value }).IsUnique();
        });
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.ToTable("Workspaces", table => table.HasCheckConstraint("CK_Workspaces_Name", "length(btrim(\"Name\")) BETWEEN 1 AND 200"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Plan).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.MonthlyMessageCap).IsRequired();
            entity.Property(x => x.UsedMessagesThisMonth).IsRequired();
            entity.Property(x => x.StorageLimitBytes).IsRequired();
            entity.Property(x => x.UsedStorageBytes).IsRequired();
            entity.Property(x => x.PlaygroundMonthlyCap).HasDefaultValue(200).IsRequired();
            entity.Property(x => x.UsedPlaygroundMessagesThisMonth).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.WidgetTitle).HasMaxLength(200).IsRequired();
            entity.Property(x => x.WidgetSubtitle).HasMaxLength(200).HasDefaultValue("Genel destek • Online").IsRequired();
            entity.Property(x => x.BrandName).HasMaxLength(200);
            entity.Property(x => x.PrimaryColor).HasMaxLength(32).IsRequired();
            entity.Property(x => x.LogoUrl);
            entity.Property(x => x.ShowBadge).IsRequired();
            entity.Property(x => x.DirectShareActive).IsRequired();
            entity.Property(x => x.DirectShareKey).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.DirectShareKey).IsUnique();
            entity.Property(x => x.UseCase).HasMaxLength(64).HasDefaultValue("ecommerce").IsRequired();
            entity.Property(x => x.AssistantRole).HasMaxLength(64).HasDefaultValue("info_desk").IsRequired();
            entity.Property(x => x.PrimaryChannel).HasMaxLength(64).HasDefaultValue("website").IsRequired();
            entity.Property(x => x.InitialKnowledgeMethod).HasMaxLength(64).HasDefaultValue("form").IsRequired();
            entity.Property(x => x.WelcomeMessage).HasMaxLength(1000).HasDefaultValue("Merhaba, sağlıklı günler diliyorum.").IsRequired();
            entity.Property(x => x.BusinessSummary).HasMaxLength(4000);
            entity.Property(x => x.KeyFaq).HasMaxLength(8000);
            entity.Property(x => x.AnswerBoundaries).HasMaxLength(4000);
            entity.Property(x => x.EscalationRules).HasMaxLength(4000);
            entity.Property(x => x.AiScheduleMode).HasMaxLength(64).HasDefaultValue("ALWAYS_AI").IsRequired();
            entity.Property(x => x.SoundNotificationEnabled).HasDefaultValue(true).IsRequired();
            entity.Property(x => x.SoundTone).HasMaxLength(64).HasDefaultValue("classic").IsRequired();
            entity.Property(x => x.AllowedDomains).HasMaxLength(2000);
            entity.Property(x => x.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<WorkspaceMember>(entity =>
        {
            entity.ToTable("WorkspaceMembers", table => table.HasCheckConstraint("CK_WorkspaceMembers_Role", "\"Role\" IN ('OWNER', 'ADMIN', 'AGENT')"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(5).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<WorkspaceInvitation>(entity =>
        {
            entity.ToTable("WorkspaceInvitations", table =>
            {
                table.HasCheckConstraint("CK_WorkspaceInvitations_Role", "\"Role\" IN ('OWNER', 'ADMIN', 'AGENT')");
                table.HasCheckConstraint("CK_WorkspaceInvitations_Lifecycle", "\"ExpiresAt\" > \"CreatedAt\" AND (\"AcceptedAt\" IS NULL OR \"RevokedAt\" IS NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(5).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.RecipientUserId });
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.InviterUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.Property(x => x.IsSuperAdmin).HasDefaultValue(false).IsRequired();
            entity.Property(x => x.PlatformRoleId);
            entity.HasOne<PlatformRole>().WithMany().HasForeignKey(x => x.PlatformRoleId).OnDelete(DeleteBehavior.SetNull);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<WorkspaceRolePermission>(entity =>
        {
            entity.ToTable("WorkspaceRolePermissions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.PermissionKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IsGranted).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
            entity.HasIndex(x => new { x.Role, x.PermissionKey }).IsUnique();
        });
        modelBuilder.Entity<PlatformRole>(entity =>
        {
            entity.ToTable("PlatformRoles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.PermissionsJson).IsRequired();
            entity.Property(x => x.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<PlanDefinition>(entity =>
        {
            entity.ToTable("PlanDefinitions");
            entity.HasKey(x => x.Plan);
            entity.Property(x => x.Plan).HasConversion<string>().HasMaxLength(16).ValueGeneratedNever();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.MonthlyMessageLimit).IsRequired();
            entity.Property(x => x.StorageLimitBytes).IsRequired();
            entity.Property(x => x.MaxSeats).IsRequired();
            entity.Property(x => x.MaxFileSizeBytes).IsRequired();
            entity.Property(x => x.MaxDocumentCount).IsRequired();
            entity.Property(x => x.MaxUrlSourceCount).IsRequired();
            entity.Property(x => x.PlaygroundMonthlyLimit).HasDefaultValue(200).IsRequired();
            entity.Property(x => x.MonthlyPriceTry).HasPrecision(18, 2).HasDefaultValue(0m).IsRequired();
            entity.Property(x => x.AllowedFeaturesJson).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });
        modelBuilder.Entity<WizardCatalogItem>(entity =>
        {
            entity.ToTable("WizardCatalogItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Category).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.Category, x.Key }).IsUnique();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.IconName).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DefaultWelcomeMessage).HasMaxLength(1000);
            entity.Property(x => x.DefaultBusinessSummary).HasMaxLength(4000);
            entity.Property(x => x.DefaultKeyFaq).HasMaxLength(8000);
            entity.Property(x => x.SystemPromptHint).HasMaxLength(2000);
            entity.Property(x => x.SortOrder).IsRequired();
            entity.Property(x => x.IsActive).HasDefaultValue(true).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
        });
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("Conversations", table =>
            {
                table.HasCheckConstraint("CK_Conversations_Status", "\"Status\" IN ('OPEN', 'RESOLVED', 'ARCHIVED')");
            });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(32).HasDefaultValue("BOT_ACTIVE").IsRequired();
            entity.Property(x => x.LastActivityAt).IsRequired();
            entity.Property(x => x.ResolvedAt);
            entity.Property(x => x.ArchivedAt);
            entity.Property(x => x.UnreadCount).HasDefaultValue(0).IsRequired();
        });
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.Phone).HasMaxLength(64);
            entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.TagsJson).HasMaxLength(2000).HasDefaultValue("[]").IsRequired();
        });
        modelBuilder.Entity<ChannelIntegration>(entity =>
        {
            entity.ToTable("ChannelIntegrations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.WorkspaceId).IsRequired();
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.Provider }).IsUnique();
            entity.Property(x => x.Status).HasMaxLength(32).HasDefaultValue("DISCONNECTED").IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(300);
            entity.Property(x => x.ExternalAccountId).HasMaxLength(256).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(x => x.SecondaryAccountId).HasMaxLength(256);
            entity.Property(x => x.AccessToken).HasMaxLength(2048).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(x => x.AppSecret).HasMaxLength(512);
            entity.Property(x => x.WebhookVerifyToken).HasMaxLength(128).IsRequired();
            entity.Property(x => x.VerifiedMetadataJson).HasDefaultValue("{}").IsRequired();
            entity.Property(x => x.LastTestedAt);
            entity.Property(x => x.LastWebhookReceivedAt);
            entity.Property(x => x.LastErrorMessage).HasMaxLength(2000);
            entity.Property(x => x.CreatedAt).IsRequired();
            entity.Property(x => x.UpdatedAt).IsRequired();
            entity.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ExternalIdentity>(entity =>
        {
            entity.ToTable("ExternalIdentities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Issuer).HasMaxLength(512).UseCollation("C").IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(255).UseCollation("C").IsRequired();
            entity.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
