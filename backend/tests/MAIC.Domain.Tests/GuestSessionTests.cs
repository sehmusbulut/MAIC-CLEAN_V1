using MAIC.Domain.Channels;
using MAIC.Domain.Conversations;
using MAIC.Domain.Widget;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class GuestSessionTests
{
    [Fact]
    public void Guest_provenance_and_fixed_lifetime_do_not_impersonate_a_member()
    {
        var now = DateTimeOffset.UtcNow;
        var guest = new GuestSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), 2, now);
        var conversation = Conversation.CreateGuest(guest);
        var message = Message.CreateGuest(conversation, guest.Id, Guid.NewGuid(), conversation.AdvanceMessageSequence(), "Hello");
        Assert.Equal('7', guest.Id.ToString()[14]);
        Assert.Equal(TimeSpan.FromHours(24), guest.ExpiresAt - guest.CreatedAt);
        Assert.Null(conversation.CreatedByUserId);
        Assert.Equal(guest.Id, conversation.CreatedByGuestSessionId);
        Assert.Equal(guest.Id, conversation.ClientRequestId);
        Assert.Equal(guest.ContactId, conversation.ContactId);
        Assert.Equal(MessageSenderKind.GUEST, message.SenderKind);
        Assert.Equal(guest.Id, message.SenderGuestSessionId);
        Assert.Null(message.SenderUserId);
        Assert.Null(message.AiExecutionId);
        Assert.Throws<ArgumentException>(() => Message.CreateGuest(conversation, Guid.NewGuid(), Guid.NewGuid(), 2, "Forged"));
        Assert.True(guest.IsCurrent(now, 2));
        Assert.False(guest.IsCurrent(guest.ExpiresAt, 2));
        Assert.False(guest.IsCurrent(now, 3));
        guest.Revoke(now.AddSeconds(1));
        var revoked = guest.RevokedAt;
        guest.Revoke(now.AddSeconds(2));
        Assert.Equal(revoked, guest.RevokedAt);
        Assert.False(guest.IsCurrent(now.AddSeconds(3), 2));
        Assert.True(guest.IsCurrent(now.AddSeconds(3), 2, allowRevoked: true));
    }

    [Fact]
    public void Only_disable_transitions_advance_channel_generation()
    {
        var channel = new Channel(Guid.NewGuid(), "Web");
        channel.SetEnabled(true);
        Assert.Equal(0, channel.GuestAccessGeneration);
        channel.SetEnabled(false);
        channel.SetEnabled(false);
        channel.SetEnabled(true);
        Assert.Equal(1, channel.GuestAccessGeneration);
        channel.SetEnabled(false);
        Assert.Equal(2, channel.GuestAccessGeneration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Digest_is_canonical_and_never_accepts_raw_capabilities(string digest) =>
        Assert.Throws<ArgumentException>(() => new GuestSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), digest, 0, DateTimeOffset.UtcNow));
}
