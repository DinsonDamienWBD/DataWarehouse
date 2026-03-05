// Hardening tests for Shared findings: Message
// Finding: 33 (LOW) All-mutable properties with no validation
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for Message hardening findings.
/// </summary>
public class MessageTests
{
    /// <summary>
    /// Finding 33: Message properties were all-mutable with no validation.
    /// Fixed by adding null validation on Id and Command properties.
    /// </summary>
    [Fact]
    public void Finding033_MessageIdCannotBeNull()
    {
        var msg = new Message();
        Assert.NotNull(msg.Id);

        Assert.Throws<ArgumentNullException>(() => msg.Id = null!);
    }

    [Fact]
    public void Finding033_MessageCommandCannotBeNull()
    {
        var msg = new Message();
        Assert.Equal(string.Empty, msg.Command);

        Assert.Throws<ArgumentNullException>(() => msg.Command = null!);
    }

    [Fact]
    public void Finding033_MessageValidPropertiesWork()
    {
        var msg = new Message
        {
            Id = "test-id",
            Command = "storage.list",
            Type = MessageType.Request
        };

        Assert.Equal("test-id", msg.Id);
        Assert.Equal("storage.list", msg.Command);
        Assert.Equal(MessageType.Request, msg.Type);
    }
}
