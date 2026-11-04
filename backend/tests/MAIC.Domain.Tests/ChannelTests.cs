using MAIC.Domain.Channels;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class ChannelTests
{
    [Fact]
    public void Lifecycle_preserves_tenant_identity_and_creation_time()
    {
        var workspaceId = Guid.CreateVersion7();
        var channel = new Channel(workspaceId, "  Website  ");
        Assert.Equal('7', channel.Id.ToString()[14]);
        Assert.Equal(ChannelType.WEB, channel.Type);
        Assert.Equal("Website", channel.Name);
        Assert.True(channel.Enabled);
        Assert.True(Channel.IsValidPublishableToken(channel.PublishableToken));
        Assert.Equal(32, Convert.FromBase64String(channel.PublishableToken.Replace('-', '+').Replace('_', '/') + "=").Length);
        var token = channel.PublishableToken;
        var created = channel.CreatedAt;
        channel.SetEnabled(false);
        channel.Rename("  Renamed  ");
        Assert.False(channel.Enabled);
        channel.SetEnabled(true);
        Assert.Equal(workspaceId, channel.WorkspaceId);
        Assert.Equal(token, channel.PublishableToken);
        Assert.Equal(created, channel.CreatedAt);
        Assert.Equal("Renamed", channel.Name);
    }

    [Fact]
    public void Names_reject_unsupported_text_before_mutating_the_channel()
    {
        Assert.Throws<ArgumentException>(() => new Channel(Guid.Empty, "Name"));
        var channel = new Channel(Guid.CreateVersion7(), "Original");
        foreach (var invalid in new[] { "", " ", "\t", "bad\0name", "bad\nname", "\ud800", "\udc00", new string('n', 201) })
        {
            Assert.False(Channel.IsValidName(invalid));
            Assert.Throws<ArgumentException>(() => new Channel(channel.WorkspaceId, invalid));
            Assert.Throws<ArgumentException>(() => channel.Rename(invalid));
            Assert.Equal("Original", channel.Name);
        }
        Assert.False(Channel.IsValidName(null));
        Assert.True(Channel.IsValidName(new string('n', 200)));
        Assert.True(Channel.IsValidName("Support \ud83d\ude42"));
    }

    [Fact]
    public void Public_identity_validation_accepts_only_the_bounded_url_safe_alphabet()
    {
        Assert.False(Channel.IsValidPublishableToken(null));
        foreach (var invalid in new[] { "", new string('a', 42), new string('a', 44), new string('a', 42) + "/", new string('a', 42) + "=" })
            Assert.False(Channel.IsValidPublishableToken(invalid));
        Assert.True(Channel.IsValidPublishableToken(new string('a', 40) + "-_9"));
    }
}
