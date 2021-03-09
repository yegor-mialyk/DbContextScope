/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EntityFrameworkCore.DbContextScope
{
    /// <summary>
    ///     As its name suggests, DbContextCollection maintains a collection of DbContext instances.
    ///     What it does in a nutshell:
    ///     - Lazily instantiates DbContext instances when its Get Of TDbContext () method is called
    ///     (and optionally starts an explicit database transaction).
    ///     - Keeps track of the DbContext instances it created so that it can return the existing
    ///     instance when asked for a DbContext of a specific type.
    ///     - Takes care of committing / rolling back changes and transactions on all the DbContext
    ///     instances it created when its Commit() or Rollback() method is called.
    /// </summary>
    public class DbContextCollection
    {
        private readonly IDbContextFactory? _dbContextFactory;
        private readonly IsolationLevel? _isolationLevel;
        private readonly bool _readOnly;
        private readonly Dictionary<DbContext, IDbContextTransaction> _transactions;
        private bool _completed;
        private bool _disposed;

        public DbContextCollection(bool readOnly = false, IsolationLevel? isolationLevel = null,
            IDbContextFactory? dbContextFactory = null)
        {
            InitializedDbContexts = new Dictionary<Type, DbContext>();
            _transactions = new Dictionary<DbContext, IDbContextTransaction>();

            _readOnly = readOnly;
            _isolationLevel = isolationLevel;
            _dbContextFactory = dbContextFactory;
        }

        public Dictionary<Type, DbContext> InitializedDbContexts { get; }

        public TDbContext Get<TDbContext>() where TDbContext : DbContext
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextCollection));

            var requestedType = typeof(TDbContext);

            if (InitializedDbContexts.ContainsKey(requestedType))
                return (TDbContext)InitializedDbContexts[requestedType];

            var dbContext = _dbContextFactory != null
                ? _dbContextFactory.Create<TDbContext>()
                : Activator.CreateInstance<TDbContext>();

            InitializedDbContexts.Add(requestedType, dbContext);

            dbContext.ChangeTracker.AutoDetectChangesEnabled = !_readOnly;

            if (_isolationLevel.HasValue)
            {
                var transaction = dbContext.Database.BeginTransaction(_isolationLevel.Value);
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

            // Best effort. You'll note that we're not actually implementing an atomic commit
            // here. It entirely possible that one DbContext instance will be committed successfully
            // and another will fail. Implementing an atomic commit would require us to wrap
            // all of this in a TransactionScope. The problem with TransactionScope is that
            // the database transaction it creates may be automatically promoted to a
            // distributed transaction if our DbContext instances happen to be using different
            // databases. And that would require the DTC service (Distributed Transaction Coordinator)
            // to be enabled on all of our live and dev servers as well as on all of our dev workstations.
            // Otherwise the whole thing would blow up at runtime.

            // In practice, if our services are implemented following a reasonably DDD approach,
            // a business transaction (i.e. a service method) should only modify entities in a single
            // DbContext. So we should never find ourselves in a situation where two DbContext instances
            // contain uncommitted changes here. We should therefore never be in a situation where the below
            // would result in a partial commit.

            ExceptionDispatchInfo? lastError = null;

            var changeCount = 0;

            foreach (var dbContext in InitializedDbContexts.Values)
                try
                {
                    if (!_readOnly)
                        changeCount += dbContext.SaveChanges();

                    // If we've started an explicit database transaction, time to commit it now.
                    if (_transactions.TryGetValue(dbContext, out var transaction))
                    {
                        transaction.Commit();
                        transaction.Dispose();
                    }
                }
                catch (Exception e)
                {
                    lastError = ExceptionDispatchInfo.Capture(e);
                }

            _transactions.Clear();
            _completed = true;

            lastError?.Throw(); // Re-throw while maintaining the exception's original stack track

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

            var c = 0;

            foreach (var dbContext in InitializedDbContexts.Values)
                try
                {
                    if (!_readOnly)
                        c += await dbContext.SaveChangesAsync(cancelToken).ConfigureAwait(false);

                    if (_transactions.TryGetValue(dbContext, out var transaction))
                    {
                        transaction.Commit();
                        transaction.Dispose();
                    }
                }
                catch (Exception e)
                {
                    lastError = ExceptionDispatchInfo.Capture(e);
                }

            _transactions.Clear();
            _completed = true;

            lastError?.Throw(); // Re-throw while maintaining the exception's original stack track

            return c;
        }

        public void Rollback()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextCollection));
            if (_completed)
                throw new InvalidOperationException(
                    "You can't call Commit() or Rollback() more than once on a DbContextCollection. All the changes in the DbContext instances managed by this collection have already been saved or rollback and all database transactions have been completed and closed. If you wish to make more data changes, create a new DbContextCollection and make your changes there.");

            ExceptionDispatchInfo? lastError = null;

            foreach (var dbContext in InitializedDbContexts.Values)
            {
                // There's no need to explicitly rollback changes in a DbContext as
                // DbContext doesn't save any changes until its SaveChanges() method is called.
                // So "rolling back" for a DbContext simply means not calling its SaveChanges()
                // method.

                // But if we've started an explicit database transaction, then we must roll it back.
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

            lastError?.Throw(); // Re-throw while maintaining the exception's original stack track
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Do our best here to dispose as much as we can even if we get errors along the way.
            // Now is not the time to throw. Correctly implemented applications will have called
            // either Commit() or Rollback() first and would have got the error there.

            if (!_completed)
                try
                {
                    if (_readOnly) Commit();
                    else Rollback();
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }

            foreach (var dbContext in InitializedDbContexts.Values)
                try
                {
                    dbContext.Dispose();
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                }

            InitializedDbContexts.Clear();
            _disposed = true;
        }
    }
}
