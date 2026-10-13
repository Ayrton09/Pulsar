using System.Data.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>
/// Puts a concurrent change at an exact point of a panel's database work: an action run once, right before or right
/// after the save of a context that matches, right after the commit of a transaction of a context that matches, or right
/// after a query whose text matches (its rows are those of before). Armed again for each use.
/// </summary>
public sealed class DatabaseHook : DbCommandInterceptor, ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly Lock _lock = new();
    private Func<DbContext, bool>? _beforeSave;
    private Func<DbContext, bool>? _afterSave;
    private Func<DbContext, bool>? _afterCommit;
    private string? _afterQuery;
    private Func<Task>? _action;

    public void BeforeSave(Func<DbContext, bool> when, Func<Task> action) => Arm(() => _beforeSave = when, action);

    public void AfterSave(Func<DbContext, bool> when, Func<Task> action) => Arm(() => _afterSave = when, action);

    public void AfterCommit(Func<DbContext, bool> when, Func<Task> action) => Arm(() => _afterCommit = when, action);

    public void AfterQuery(string sqlFragment, Func<Task> action) => Arm(() => _afterQuery = sqlFragment, action);

    async ValueTask<InterceptionResult<int>> ISaveChangesInterceptor.SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken)
    {
        if (eventData.Context is { } context && Take(ref _beforeSave, context) is { } action)
        {
            await action();
        }

        return result;
    }

    async ValueTask<int> ISaveChangesInterceptor.SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken)
    {
        if (eventData.Context is { } context && Take(ref _afterSave, context) is { } action)
        {
            await action();
        }

        return result;
    }

    async Task IDbTransactionInterceptor.TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken)
    {
        if (eventData.Context is { } context && Take(ref _afterCommit, context) is { } action)
        {
            await action();
        }
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Func<Task>? action = null;
        lock (_lock)
        {
            if (_afterQuery is { } fragment && command.CommandText.Contains(fragment, StringComparison.Ordinal))
            {
                (action, _action, _afterQuery) = (_action, null, null);
            }
        }

        if (action is not null)
        {
            await action();
        }

        return result;
    }

    private void Arm(Action set, Func<Task> action)
    {
        lock (_lock)
        {
            set();
            _action = action;
        }
    }

    private Func<Task>? Take(ref Func<DbContext, bool>? when, DbContext context)
    {
        lock (_lock)
        {
            if (when is null || !when(context))
            {
                return null;
            }

            var action = _action;
            (when, _action) = (null, null);
            return action;
        }
    }
}

public static class DatabaseHookExtensions
{
    /// <summary>Another instance of the panel, on the same database, whose database work goes through <paramref name="hook"/>.</summary>
    public static WebApplicationFactory<Program> WithDatabaseHook(this WebApplicationFactory<Program> factory, DatabaseHook hook) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<PulsarDbContext>(options => options.AddInterceptors(hook))));
}
