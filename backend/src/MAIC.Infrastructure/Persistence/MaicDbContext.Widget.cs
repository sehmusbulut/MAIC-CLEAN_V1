using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Widget;
using MAIC.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed partial class MaicDbContext
{
    private static void ConfigureWidget(ModelBuilder model)
    {
        model.Entity<Channel>(e =>
        {
            e.Property(x => x.GuestAccessGeneration).HasDefaultValue(0L);
            e.ToTable("Channels", t => t.HasCheckConstraint("CK_Channels_GuestGeneration", "\"GuestAccessGeneration\" >= 0"));
        });
        model.Entity<GuestSession>(e =>
        {
            e.ToTable("GuestSessions", t =>
            {
                t.HasCheckConstraint("CK_GuestSessions_Digest", "\"CredentialHash\" ~ '^[0-9A-F]{64}$'");
                t.HasCheckConstraint("CK_GuestSessions_Lifecycle", Sql("""
                    "ChannelGeneration" >= 0 AND "ExpiresAt" = "CreatedAt" + interval '24 hours'
                    AND ("RevokedAt" IS NULL OR "RevokedAt" >= "CreatedAt")
                    """));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CredentialHash).HasMaxLength(64).UseCollation("C").IsRequired();
            e.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            e.HasAlternateKey(x => new { x.WorkspaceId, x.ChannelId, x.ContactId, x.Id });
            e.HasIndex(x => x.CredentialHash).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.ChannelId, x.CreatedAt });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Channel>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ChannelId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Contact>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ContactId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Conversation>(e =>
        {
            e.ToTable("Conversations", t => t.HasCheckConstraint("CK_Conversations_Creator", Sql("""
                ("CreatedByUserId" IS NOT NULL AND "CreatedByGuestSessionId" IS NULL)
                OR ("CreatedByUserId" IS NULL AND "CreatedByGuestSessionId" IS NOT NULL AND "ClientRequestId" = "CreatedByGuestSessionId")
                """)));
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedByGuestSessionId }).IsUnique();
            // PostgreSQL permits nullable columns in a unique principal index. A migration adds
            // the guest-message ownership FK; an EF alternate key would make member creators required.
            e.HasIndex(x => new { x.WorkspaceId, x.Id, x.CreatedByGuestSessionId }).IsUnique();
            e.HasOne<GuestSession>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ChannelId, x.ContactId, x.CreatedByGuestSessionId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ChannelId, x.ContactId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Message>(e =>
        {
            e.ToTable("Messages", t => t.HasCheckConstraint("CK_Messages_Sender", Sql("""
                ("SenderKind" = 'MEMBER' AND "SenderUserId" IS NOT NULL AND "AiExecutionId" IS NULL AND "SenderGuestSessionId" IS NULL)
                OR ("SenderKind" = 'AI' AND "SenderUserId" IS NULL AND "AiExecutionId" IS NOT NULL AND "SenderGuestSessionId" IS NULL AND "ClientMessageId" = "AiExecutionId")
                OR ("SenderKind" = 'GUEST' AND "SenderUserId" IS NULL AND "AiExecutionId" IS NULL AND "SenderGuestSessionId" IS NOT NULL)
                """)));
            e.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.SenderGuestSessionId, x.ClientMessageId }).IsUnique();
            e.HasOne<GuestSession>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.SenderGuestSessionId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
