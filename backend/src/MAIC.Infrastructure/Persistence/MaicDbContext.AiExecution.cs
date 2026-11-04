using MAIC.Domain.AI;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed partial class MaicDbContext
{
    private static void ConfigureAiExecution(ModelBuilder model)
    {
        model.Entity<Conversation>(e =>
        {
            e.ToTable("Conversations", t => t.HasCheckConstraint("CK_Conversations_AiRevision", "\"AiBindingRevision\" >= 0"));
            e.HasOne<AiAgent>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.AiAgentId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Message>(e =>
        {
            e.Property(x => x.SenderKind).HasConversion<string>().HasMaxLength(6).HasDefaultValue(MessageSenderKind.MEMBER);
            e.HasAlternateKey(x => new { x.WorkspaceId, x.ConversationId, x.Id });
            e.ToTable("Messages", t => t.HasCheckConstraint("CK_Messages_Sender", Sql("""
                ("SenderKind" = 'MEMBER' AND "SenderUserId" IS NOT NULL AND "AiExecutionId" IS NULL)
                OR ("SenderKind" = 'AI' AND "SenderUserId" IS NULL AND "AiExecutionId" IS NOT NULL AND "ClientMessageId" = "AiExecutionId")
                """)));
            e.HasIndex(x => x.AiExecutionId).IsUnique();
            e.HasOne<AiExecution>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId, x.AiExecutionId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<HandoffRequest>(e =>
        {
            e.HasIndex(x => x.AiExecutionId).IsUnique();
            e.HasOne<AiExecution>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId, x.AiExecutionId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AiExecution>(e =>
        {
            e.ToTable("AiExecutions", t =>
            {
                t.HasCheckConstraint("CK_AiExecutions_Lifecycle", Sql("""
                    "ExpiresAt" > "CreatedAt" AND (("State" = 'IN_PROGRESS' AND "CompletedAt" IS NULL AND "Outcome" IS NULL)
                    OR ("State" = 'TERMINAL' AND "CompletedAt" IS NOT NULL AND "Outcome" IS NOT NULL
                        AND "Outcome" IN ('ANSWERED','FALLBACK','HANDOFF','UNAVAILABLE','SUPERSEDED','CANCELLED')))
                    """));
                t.HasCheckConstraint("CK_AiExecutions_Usage", Sql("""
                    "ReservedCost" >= 0 AND ("ActualCost" IS NULL OR "ActualCost" >= 0)
                    AND ("InputTokens" IS NULL OR "InputTokens" >= 0) AND ("OutputTokens" IS NULL OR "OutputTokens" >= 0)
                    AND ("GenerationReservedAt" IS NOT NULL OR ("ReservedCost" = 0 AND NOT "ProviderAttempted"))
                    """));
                t.HasCheckConstraint("CK_AiExecutions_Fences", "\"ConversationRevision\" >= 0 AND \"MessageSequence\" > 0 AND \"AssignmentSequence\" >= 0 AND \"HandoffSequence\" >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasAlternateKey(x => new { x.WorkspaceId, x.ConversationId, x.Id });
            e.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.TriggerMessageId }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasIndex(x => x.GenerationReservedAt);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(11);
            e.Property(x => x.Outcome).HasMaxLength(16);
            e.Property(x => x.Failure).HasMaxLength(40);
            e.Property(x => x.RetrievalOutcome).HasMaxLength(20);
            e.Property(x => x.RetrievalReason).HasMaxLength(40);
            e.Property(x => x.AgentSnapshot).HasColumnType("text").IsRequired();
            e.Property(x => x.CitationsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ReservedCost).HasPrecision(20, 9);
            e.Property(x => x.ActualCost).HasPrecision(20, 9);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Message>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ConversationId, x.TriggerMessageId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ConversationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
