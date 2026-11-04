using MAIC.Domain.Conversations;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class ConversationTests
{
    [Fact]
    public void Conversation_binds_required_ids_starts_open_and_sequences_without_changing_identity()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        var conversation = new Conversation(ids[0], ids[1], ids[2], ids[3], ids[4]);
        Assert.Equal('7', conversation.Id.ToString()[14]);
        Assert.Equal(ConversationState.OPEN, conversation.Status);
        Assert.Equal(ids, new[] { conversation.WorkspaceId, conversation.ChannelId, conversation.ContactId, conversation.CreatedByUserId!.Value, conversation.ClientRequestId });
        var originalId = conversation.Id;
        var createdAt = conversation.CreatedAt;
        Assert.Equal(0, conversation.LastMessageSequence);
        Assert.Equal(1, conversation.AdvanceMessageSequence());
        Assert.Equal(2, conversation.AdvanceMessageSequence());
        Assert.Equal(originalId, conversation.Id);
        Assert.Equal(createdAt, conversation.CreatedAt);
        for (var i = 0; i < ids.Length; i++)
        {
            var invalid = ids.ToArray();
            invalid[i] = Guid.Empty;
            Assert.Throws<ArgumentException>(() => new Conversation(invalid[0], invalid[1], invalid[2], invalid[3], invalid[4]));
        }
    }

    [Fact]
    public void Message_preserves_multiline_text_and_counts_UTF16_units()
    {
        const string content = "  First\r\n\tSecond \ud83d\ude42  ";
        var message = NewMessage(content);
        Assert.Equal('7', message.Id.ToString()[14]);
        Assert.Equal(content, message.Content);
        Assert.Equal(1, message.Sequence);
        Assert.True(Message.IsValidContent(new string('x', 4000)));
        Assert.True(Message.IsValidContent(string.Concat(Enumerable.Repeat("\ud83d\ude42", 2000))));
        Assert.False(Message.IsValidContent(string.Concat(Enumerable.Repeat("\ud83d\ude42", 2001))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Message(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, "Text"));
        Assert.Throws<ArgumentException>(() => new Message(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 1, "Text"));
    }

    [Fact]
    public void Message_rejects_blank_invalid_surrogates_and_every_unsupported_control()
    {
        foreach (var text in new[] { "", " \r\n\t", "\u00a0\u2007", "\ud800", "\udc00", "\ud800x", new string('x', 4001) })
        {
            Assert.False(Message.IsValidContent(text));
            Assert.Throws<ArgumentException>(() => NewMessage(text));
        }
        Assert.False(Message.IsValidContent(null));
        for (var code = 0; code <= 0x9f; code++)
        {
            var c = (char)code;
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            {
                Assert.False(Message.IsValidContent("Text" + c));
                Assert.Throws<ArgumentException>(() => NewMessage("Text" + c));
            }
        }
    }

    private static Message NewMessage(string text) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, text);
}
