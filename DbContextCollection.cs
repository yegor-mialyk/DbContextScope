//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2025, Yegor Mialyk. All Rights Reserved.
//
// Licensed under the MIT License. See the LICENSE file for details.
//

using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.DbContextScope;

public sealed class DbContextCollection
{
    private readonly IDbContextFactory? _dbContextFactory;
    private readonly IsolationLevel _isolationLevel;
    private readonly ILogger<DbContextCollection> _logger;
    private readonly bool _readOnly;
    private readonly Dictionary<DbContext, IDbContextTransaction> _transactions = new();
    private bool _completed;
    private bool _disposed;
    private readonly Dictionary<Type, DbContext> _initializedDbContexts = new();

    public DbContextCollection(ILoggerFactory loggerFactory, bool readOnly = false,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        IDbContextFactory? dbContextFactory = null)
    {
        _logger = loggerFactory.CreateLogger<DbContextCollection>();
        _readOnly = readOnly;
        _isolationLevel = isolationLevel;
        _dbContextFactory = dbContextFactory;
    }

    public TDbContext Get<TDbContext>() where TDbContext : DbContext
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextCollection));

        var requestedType = typeof(TDbContext);

        if (_initializedDbContexts.TryGetValue(requestedType, out var context))
            return (TDbContext)context;

        var dbContext = _dbContextFactory is not null
            ? _dbContextFactory.Create<TDbContext>()
            : Activator.CreateInstance<TDbContext>();

        _initializedDbContexts.Add(requestedType, dbContext);

        dbContext.ChangeTracker.AutoDetectChangesEnabled = !_readOnly;

        if (_isolationLevel == IsolationLevel.Unspecified)
            return dbContext;

        var transaction = dbContext.Database.BeginTransaction(_isolationLevel);
        _transactions.Add(dbContext, transaction);

        return dbContext;
    }

    public int Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextCollection));

        if (_completed)
            throw new InvalidOperationException("You cannot call Commit() more than once on a DbContextCollection.");

        var changeCount = 0;

        foreach (var dbContext in _initializedDbContexts.Values)
            try
            {
                if (!_readOnly)
                    changeCount += dbContext.SaveChanges();

                if (!_transactions.TryGetValue(dbContext, out var transaction))
                    continue;

                transaction.Commit();
                transaction.Dispose();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error saving changes to {DbContext}", dbContext.GetType().Name);
            }

        _transactions.Clear();

        _completed = true;

        return changeCount;
    }

    public async Task<int> CommitAsync(CancellationToken cancelToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextCollection));

        if (_completed)
            throw new InvalidOperationException("You cannot call Commit() more than once on a DbContextCollection.");

        var changeCount = 0;

        foreach (var dbContext in _initializedDbContexts.Values)
            try
            {
                if (!_readOnly)
                    changeCount += await dbContext.SaveChangesAsync(cancelToken).ConfigureAwait(false);

                if (!_transactions.TryGetValue(dbContext, out var transaction))
                    continue;

                await transaction.CommitAsync(cancelToken).ConfigureAwait(false);

                transaction.Dispose();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error saving changes to {DbContext}", dbContext.GetType().Name);
            }

        _transactions.Clear();

        _completed = true;

        return changeCount;
    }

    private void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextCollection));

        if (_completed)
            throw new InvalidOperationException(
                "You can't call Commit() or Rollback() more than once on a DbContextCollection. All the changes in the DbContext instances managed by this collection have already been saved or rollback and all database transactions have been completed and closed. If you wish to make more data changes, create a new DbContextCollection and make your changes there.");

        ExceptionDispatchInfo? lastError = null;

        foreach (var dbContext in _initializedDbContexts.Values)
        {
            if (!_transactions.TryGetValue(dbContext, out var transaction))
                continue;

            try
            {
                transaction.Rollback();
                transaction.Dispose();
            }
            catch (Exception e)
            {
                lastError = ExceptionDispatchInfo.Capture(e);
            }
        }

        _transactions.Clear();
        _completed = true;

        lastError?.Throw();
    }

    public void DisposeCollection()
    {
        if (_disposed)
            return;

        if (!_completed)
            try
            {
                if (_readOnly)
                    Commit();
                else
                    Rollback();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error commiting/rolling back DbContextCollection");
            }

        foreach (var dbContext in _initializedDbContexts.Values)
            try
            {
                dbContext.Dispose();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error disposing {DbContext}", dbContext.GetType().Name);
            }

        _initializedDbContexts.Clear();
        _disposed = true;
    }
}
