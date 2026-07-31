using AzureNamingTool.Data;
using AzureNamingTool.Models;
using AzureNamingTool.Repositories;
using AzureNamingTool.Services;
using AzureNamingTool.Services.Interfaces;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AzureNamingTool.UnitTests.Services;

/// <summary>
/// Covers concurrent name generation against a real SQLite file, which is where
/// the collection-wide rewrite used to fail requests and drop rows.
/// </summary>
public class GeneratedNamesServiceConcurrencyTests : IDisposable
{
    private readonly string _databasePath;
    private readonly ICacheService _cacheService;

    public GeneratedNamesServiceConcurrencyTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ant-concurrency-{Guid.NewGuid():N}.db");

        // The app registers the cache as a singleton and everything else per request
        _cacheService = new CacheService(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CacheService>.Instance);

        using var dbContext = new ConfigurationDbContext(_databasePath);
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task PostItemAsync_ShouldPersistEveryName_WhenCalledConcurrently()
    {
        const int callers = 25;

        var responses = await Task.WhenAll(Enumerable.Range(0, callers).Select(i => Task.Run(async () =>
        {
            // A scoped service and DbContext per caller, matching a request scope
            using var dbContext = new ConfigurationDbContext(_databasePath);
            var repository = new SQLiteConfigurationRepository<GeneratedName>(dbContext, _cacheService);
            var service = new GeneratedNamesService(repository, Mock.Of<IAdminLogService>());

            return await service.PostItemAsync(new GeneratedName
            {
                ResourceName = $"stnakapidevusw3{i:D3}",
                ResourceTypeName = "Storage account",
                User = "concurrency-test",
                CreatedOn = DateTime.UtcNow,
                Components = [],
            });
        })));

        responses.Should().OnlyContain(x => x.Success, "a concurrent caller must not fail name generation");

        using var verifyContext = new ConfigurationDbContext(_databasePath);
        var stored = await verifyContext.GeneratedNames.AsNoTracking().ToListAsync();

        stored.Should().HaveCount(callers, "no caller's name may be overwritten by another");
        stored.Select(x => x.Id).Should().OnlyHaveUniqueItems();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _cacheService.ClearAllCache();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }
    }
}
