using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server.Library;
using Xunit;

public class WebUsageTests
{
    [Fact]
    public void AnOlderSingleTotalCountsAsExasAndParallelStartsAtZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-usage-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var month = DateTime.UtcNow.ToString("yyyy-MM");
            File.WriteAllText(Path.Combine(dir, "web-lookups.json"), $"{{\"Month\":\"{month}\",\"Lookups\":1287,\"Dollars\":9.01}}");
            var web = new WebLookup(null!, NullLogger<WebLookup>.Instance, new DataDirectory(dir));
            Assert.Equal(9.01, web.ExaMonth.Dollars, 2);
            Assert.Equal(0, web.ParallelMonth.Dollars);
            Assert.Equal(1287, web.ThisMonth.Lookups);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
