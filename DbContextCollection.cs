//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2026, Yegor Mialyk. All Rights Reserved.
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
        dbContext.ChangeTracker.QueryTrackingBehavior =
            _readOnly ? QueryTrackingBehavior.NoTracking : QueryTrackingBehavior.TrackAll;

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

        ExceptionDispatchInfo? lastError = null;

        var changeCount = 0;

        foreach (var dbContext in _initializedDbContexts.Values)
            try
            {
                if (_readOnly)
                    continue;

                changeCount += dbContext.SaveChanges();

                if (!_transactions.TryGetValue(dbContext, out var transaction))
                    continue;

                transaction.Commit();
                transaction.Dispose();

                _transactions.Remove(dbContext);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error saving changes to {DbContext}", dbContext.GetType().Name);

                lastError = ExceptionDispatchInfo.Capture(e);
            }

        if (lastError is not null)
            lastError.Throw();
        else
            _completed = true;

        return changeCount;
    }

    public async Task<int> CommitAsync(CancellationToken cancelToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextCollection));

        if (_completed)
            throw new InvalidOperationException("You cannot call Commit() more than once on a DbContextCollection.");

        ExceptionDispatchInfo? lastError = null;

        var changeCount = 0;

        foreach (var dbContext in _initializedDbContexts.Values)
            try
            {
                if (_readOnly)
                    continue;

                changeCount += await dbContext.SaveChangesAsync(cancelToken).ConfigureAwait(false);

                if (!_transactions.TryGetValue(dbContext, out var transaction))
                    continue;

                await transaction.CommitAsync(cancelToken).ConfigureAwait(false);

                transaction.Dispose();

                _transactions.Remove(dbContext);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error saving changes to {DbContext}", dbContext.GetType().Name);

                lastError = ExceptionDispatchInfo.Capture(e);
            }

        if (lastError is not null)
            lastError.Throw();
        else
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
                transaction.Dispose();

                _transactions.Remove(dbContext);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error rolling back transaction for {DbContext}", dbContext.GetType().Name);

                lastError = ExceptionDispatchInfo.Capture(e);
            }
        }

        if (lastError is not null)
            lastError.Throw();
        else
            _completed = true;
    }

    public void DisposeCollection()
    {
        if (_disposed)
            return;

        if (!_completed)
            try
            {
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
