using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Access;

/// <summary>A change made while a user's permissions are being read is never hidden behind the cache.</summary>
public sealed class PermissionCacheTests(PermissionCacheTests.HookedCacheFactory factory) : IClassFixture<PermissionCacheTests.HookedCacheFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AChangeWhileLoadingIsSeenAtTheNextCheck()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using var admin = scenario.Admin;
        var role = await scenario.CreateRoleAsync("cached-role", Permissions.Server.View);
        await scenario.CreateUserAsync("cached-user", role);
        UserId user;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            user = (await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Users.SingleAsync(u => u.UserName == "cached-user", Ct)).Id;
        }

        // Between reading the user's roles and caching them, the role is taken away (and the cache told so).
        factory.Cache.BeforeNextEntry = () =>
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<PulsarDbContext>().UserRoles.Where(a => a.UserId == user).ExecuteDelete();
            scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>().Invalidate(user);
        };

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>().GetAsync(user, Ct)).Has(Permissions.Server.View, AccessTarget.Global));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.False((await scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>().GetAsync(user, Ct)).Has(Permissions.Server.View, AccessTarget.Global));
        }
    }

    /// <summary>The panel, with a memory cache that runs a hook just before its next entry is made.</summary>
    public sealed class HookedCacheFactory : PulsarApiFactory
    {
        public HookedCache Cache { get; } = new(new MemoryCache(Options.Create(new MemoryCacheOptions())));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IMemoryCache>(Cache));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cache.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public sealed class HookedCache(IMemoryCache inner) : IMemoryCache
    {
        public Action? BeforeNextEntry { get; set; }

        public ICacheEntry CreateEntry(object key)
        {
            var hook = BeforeNextEntry;
            BeforeNextEntry = null;
            hook?.Invoke();
            return inner.CreateEntry(key);
        }

        public void Remove(object key) => inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);

        public void Dispose() => inner.Dispose();
    }
}
