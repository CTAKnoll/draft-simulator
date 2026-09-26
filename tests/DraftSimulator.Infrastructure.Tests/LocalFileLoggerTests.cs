using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class LocalFileLoggerTests
{
    [Fact]
    public void WritesOnlyTimestampEventAndNumericErrorCode()
    {
        using var temporary = new TemporaryDirectory();
        var logger = new RollingLocalFileLogger(temporary.Combine("Logs"));

        logger.Write("session.error", 18);

        var text = File.ReadAllText(temporary.Combine("Logs", "draft-simulator.log"));
        Assert.Contains(" session.error error=18", text);
        Assert.Throws<ArgumentException>(() => logger.Write("room ABCDE-FGHJK"));
    }
}
