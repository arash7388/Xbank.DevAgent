using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class RunEventTests
{
    [Fact]
    public void RecordsCreationTimeForActivityQueue()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        RunEvent runEvent = RunEvent.Tool("developer", "Requested a repository edit.");

        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.InRange(runEvent.OccurredAt, before, after);
    }
}
