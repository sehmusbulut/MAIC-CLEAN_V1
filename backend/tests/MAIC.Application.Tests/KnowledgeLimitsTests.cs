using MAIC.Application.Knowledge;
using Xunit;

namespace MAIC.Application.Tests;

public sealed class KnowledgeLimitsTests
{
    [Fact]
    public void Defaults_are_valid_and_non_positive_limits_fail_fast()
    {
        Assert.NotNull(new KnowledgeLimits().Validate());
        foreach (var invalid in new KnowledgeLimits[]
                 {
                     new() { MaxStorageBytesPerWorkspace = 0 }, new() { MaxDocumentsPerWorkspace = -1 },
                     new() { MaxRunningJobsPerWorkspace = 0 }, new() { WritesPerMinutePerUser = 0 }
                 })
            Assert.Throws<InvalidOperationException>(() => invalid.Validate());
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("Text/Markdown; charset=UTF-8", "text/markdown")]
    [InlineData("application/pdf; charset=latin1", "application/pdf")]
    [InlineData("text/plain; charset=latin1", null)]
    [InlineData("text/html", null)]
    [InlineData("text/plain; broken", null)]
    [InlineData("", null)]
    public void Upload_media_types_are_parsed_strictly(string header, string? expected) =>
        Assert.Equal(expected, KnowledgeDocumentService.ParseUploadContentType(header));
}
