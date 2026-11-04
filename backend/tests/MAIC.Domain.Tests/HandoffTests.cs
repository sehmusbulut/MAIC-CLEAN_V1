using MAIC.Domain.Conversations;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class HandoffTests
{
    [Fact]
    public void Assignment_history_tracks_real_changes_across_resolution_and_reopen()
    {
        var actor = Guid.NewGuid();
        var assignee = Guid.NewGuid();
        var conversation = NewConversation();
        Assert.Null(conversation.Assign(null, actor, AssignmentReason.MANUAL));
        var assigned = conversation.Assign(assignee, actor, AssignmentReason.MANUAL)!;
        Assert.Equal('7', assigned.Id.ToString()[14]);
        Assert.Equal(1, assigned.Sequence);
        Assert.Null(assigned.PreviousAssignedUserId);
        Assert.Equal(assignee, assigned.AssignedUserId);
        Assert.Equal(actor, assigned.ActorUserId);
        Assert.Null(conversation.Assign(assignee, actor, AssignmentReason.MANUAL));
        Assert.True(conversation.Resolve());
        Assert.False(conversation.Resolve());
        Assert.Equal(assignee, conversation.AssignedUserId);
        var cleared = conversation.Assign(null, actor, AssignmentReason.MEMBER_REMOVED)!;
        Assert.Equal(assignee, cleared.PreviousAssignedUserId);
        Assert.Null(cleared.AssignedUserId);
        Assert.Equal(2, cleared.Sequence);
        Assert.True(conversation.Reopen());
        Assert.False(conversation.Reopen());
        Assert.Null(conversation.AssignedUserId);
        Assert.Equal(2, conversation.LastAssignmentSequence);
        Assert.Throws<ArgumentException>(() => conversation.Assign(Guid.Empty, actor, AssignmentReason.MANUAL));
        Assert.Throws<ArgumentException>(() => conversation.Assign(assignee, Guid.Empty, AssignmentReason.MANUAL));
        Assert.Throws<ArgumentException>(() => conversation.Assign(assignee, actor, AssignmentReason.MEMBER_REMOVED));
        Assert.Throws<ArgumentException>(() => conversation.Assign(assignee, actor, AssignmentReason.HANDOFF_ACCEPTED));
        Assert.Equal(2, conversation.LastAssignmentSequence);
    }

    [Theory]
    [InlineData(HandoffTerminalReason.ACCEPTED, HandoffState.ACCEPTED)]
    [InlineData(HandoffTerminalReason.MANUAL_ASSIGNMENT, HandoffState.CANCELLED)]
    [InlineData(HandoffTerminalReason.RESOLVED, HandoffState.CANCELLED)]
    public void Handoff_terminal_transition_is_single_and_records_trusted_actor(HandoffTerminalReason reason, HandoffState status)
    {
        var conversation = NewConversation();
        var actor = Guid.NewGuid();
        var request = conversation.RequestHandoff(actor, Guid.NewGuid());
        Assert.Equal('7', request.Id.ToString()[14]);
        Assert.Equal(1, request.Sequence);
        Assert.Equal(HandoffState.PENDING, request.Status);
        Assert.Null(request.TerminalActorUserId);
        Assert.Null(request.TerminalAt);
        Assert.Null(request.TerminalReason);
        var acceptingActor = Guid.NewGuid();
        request.Complete(acceptingActor, reason);
        Assert.Equal(status, request.Status);
        Assert.Equal(acceptingActor, request.TerminalActorUserId);
        Assert.Equal(reason, request.TerminalReason);
        Assert.True(request.TerminalAt >= request.CreatedAt);
        Assert.Throws<InvalidOperationException>(() => request.Complete(actor, HandoffTerminalReason.ACCEPTED));
        conversation.Resolve();
        Assert.Throws<InvalidOperationException>(() => conversation.RequestHandoff(actor, Guid.NewGuid()));
        Assert.Equal(1, conversation.LastHandoffSequence);
        conversation.Reopen();
        Assert.Equal(2, conversation.RequestHandoff(actor, Guid.NewGuid()).Sequence);
    }

    [Fact]
    public void Invalid_handoff_input_does_not_advance_sequence()
    {
        var conversation = NewConversation();
        Assert.Throws<ArgumentException>(() => conversation.RequestHandoff(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => conversation.RequestHandoff(Guid.NewGuid(), Guid.Empty));
        Assert.Equal(0, conversation.LastHandoffSequence);
        var request = conversation.RequestHandoff(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => request.Complete(Guid.Empty, HandoffTerminalReason.ACCEPTED));
        Assert.Throws<ArgumentException>(() => request.Complete(Guid.NewGuid(), (HandoffTerminalReason)999));
        Assert.Equal(HandoffState.PENDING, request.Status);
    }

    private static Conversation NewConversation() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
}
