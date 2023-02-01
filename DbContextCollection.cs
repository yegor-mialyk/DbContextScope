/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.DbContextScope;

public class DbContextCollection
{
    private readonly IDbContextFactory? _dbContextFactory;
    private readonly IsolationLevel _isolationLevel;
    private readonly ILogger<DbContextScope> _logger;
    private readonly bool _readOnly;
    private readonly Dictionary<DbContext, IDbContextTransaction> _transactions = new();
    private bool _completed;
    private bool _disposed;
    private readonly Dictionary<Type, DbContext> _initializedDbContexts = new();

    public DbContextCollection(ILogger<DbContextScope> logger, bool readOnly = false,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        IDbContextFactory? dbContextFactory = null)
    {
        _logger = logger;
        _readOnly = readOnly;
        _isolationLevel = isolationLevel;
        _dbContextFactory = dbContextFactory;
    }

    public TDbContext Get<TDbContext>() where TDbContext : DbContext
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbContextCollection));

        var requestedType = typeof(TDbContext);

        if (_initializedDbContexts.TryGetValue(requestedType, out var context))
            return (TDbContext)context;

        var dbContext = _dbContextFactory is not null
            ? _dbContextFactory.Create<TDbContext>()
            : Activator.CreateInstance<TDbContext>();

        _initializedDbContexts.Add(requestedType, dbContext);

        dbContext.ChangeTracker.AutoDetectChangesEnabled = !_readOnly;

        if (_isolationLevel != IsolationLevel.Unspecified)
        {
            var transaction = dbContext.Database.BeginTransaction(_isolationLevel);
            _transactions.Add(dbContext, transaction);
        }

        return dbContext;
    }

    public int Commit()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbContextCollection));
        if (_completed)
            throw new InvalidOperationException(
                "You can't call Commit() or Rollback() more than once on a DbContextCollection. All the changes in the DbContext instances managed by this collection have already been saved or rollback and all database transactions have been completed and closed. If you wish to make more data changes, create a new DbContextCollection and make your changes there.");

        ExceptionDispatchInfo? lastError = null;

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
                lastError = ExceptionDispatchInfo.Capture(e);
            }

        _transactions.Clear();

        _completed = true;

        lastError?.Throw();

        return changeCount;
    }

    public async Task<int> CommitAsync(CancellationToken cancelToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbContextCollection));
        if (_completed)
            throw new InvalidOperationException(
                "You can't call Commit() or Rollback() more than once on a DbContextCollection. All the changes in the DbContext instances managed by this collection have already been saved or rollback and all database transactions have been completed and closed. If you wish to make more data changes, create a new DbContextCollection and make your changes there.");

        ExceptionDispatchInfo? lastError = null;

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
                lastError = ExceptionDispatchInfo.Capture(e);
            }

        _transactions.Clear();

        _completed = true;

        lastError?.Throw();

        return changeCount;
    }

    public void Rollback()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbContextCollection));
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
                _logger.LogError(e, "Error disposing DbContext");
            }

        _initializedDbContexts.Clear();
        _disposed = true;
    }
}
