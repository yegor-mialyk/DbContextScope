/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.DbContextScope
{
    public sealed class DbContextScope : IDbContextScope
    {
        private bool _completed;
        private bool _disposed;
        private readonly bool _nested;
        private readonly DbContextScope? _parentScope;
        private readonly ILogger<DbContextScope> _logger;
        private readonly DbContextScopeOption _joiningOption;
        private readonly bool _readOnly;

        public DbContextScope(ILogger<DbContextScope> logger, DbContextScopeOption joiningOption, bool readOnly = false,
            IsolationLevel isolationLevel = IsolationLevel.Unspecified,
            IDbContextFactory? dbContextFactory = null)
        {
            if (isolationLevel != IsolationLevel.Unspecified)
                joiningOption = DbContextScopeOption.CreateNew;

            _logger = logger;
            _joiningOption = joiningOption;
            _readOnly = readOnly;

            _parentScope = GetAmbientScope();

            if (joiningOption == DbContextScopeOption.Suppress)
            {
#if DEBUG
                _logger.LogDebug("Start suppressing an ambient DbContext scope");
#endif
                ambientDbContextScopeIdHolder.Value = null;
                return;
            }

            if (_parentScope != null && joiningOption == DbContextScopeOption.JoinExisting &&
                (!_parentScope._readOnly || _readOnly))
            {
#if DEBUG
                _logger.LogDebug("Join existing DbContext scope");
#endif
                _nested = true;
                DbContexts = _parentScope.DbContexts;
            }
            else
            {
#if DEBUG
                _logger.LogDebug("Start new DbContext scope");
#endif
                DbContexts = new(_readOnly, isolationLevel, dbContextFactory);
            }

            SetAmbientScope(this);
        }

        public DbContextCollection DbContexts { get; } = default!;

        public int SaveChanges()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextScope));
            if (_completed)
                throw new InvalidOperationException(
                    "You cannot call SaveChanges() more than once on a DbContextScope.");

            // Only save changes if we're not a nested scope. Otherwise, let the top-level scope
            // decide when the changes should be saved.
            var changeCount = 0;
            if (!_nested)
                changeCount = DbContexts.Commit();

            _completed = true;

            return changeCount;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancelToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextScope));
            if (_completed)
                throw new InvalidOperationException(
                    "You cannot call SaveChanges() more than once on a DbContextScope.");

            var changeCount = 0;
            if (!_nested)
                changeCount = await DbContexts.CommitAsync(cancelToken).ConfigureAwait(false);

            _completed = true;
            return changeCount;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_joiningOption == DbContextScopeOption.Suppress)
            {
                if (_parentScope != null)
                    SetAmbientScope(_parentScope);

                _disposed = true;

#if DEBUG
                _logger.LogDebug("Stop suppressing an ambient DbContext scope");
#endif

                return;
            }

#if DEBUG
            _logger.LogDebug("Leave DbContext scope");
#endif

            if (!_nested)
                DbContexts.DisposeCollection();

            // Pop ourselves from the ambient scope stack
            var currentAmbientScope = GetAmbientScope();
            if (currentAmbientScope != this)
                throw new InvalidOperationException(
                    "DbContextScope instances must be disposed of in the order in which they were created.");

            RemoveAmbientScope();

            if (_parentScope != null)
            {
                if (_parentScope._disposed && _nested)
                    throw new InvalidOperationException(
                        $@"PROGRAMMING ERROR - When attempting to dispose a DbContextScope, we found that our parent DbContextScope has already been disposed!
This means that someone started a parallel flow of execution (e.g. created a TPL task, created a thread or queued a work item on the ThreadPool)
within the context of a DbContextScope without suppressing the ambient context first.

In order to fix this:
1) Look at the stack trace below - this is the stack trace of the parallel task in question.
2) Find out where this parallel task was created.
3) Change the code so that the ambient context is suppressed before the parallel task is created. You can do this with IDbContextScopeFactory.HideContext() (wrap the parallel task creation code block in this).

{Environment.StackTrace}");

                SetAmbientScope(_parentScope);
            }

            _disposed = true;
        }

        private static readonly AsyncLocal<object?> ambientDbContextScopeIdHolder = new();

        private static readonly ConditionalWeakTable<object, DbContextScope> dbContextScopeInstances = new();

        private readonly object _instanceId = new();

        private static void SetAmbientScope(DbContextScope newAmbientScope)
        {
            if (newAmbientScope == null)
                throw new ArgumentNullException(nameof(newAmbientScope));

            var current = ambientDbContextScopeIdHolder.Value;

            if (current == newAmbientScope._instanceId)
                return;

            // Store the new scope's instance identifier in the CallContext, making it the ambient scope
            ambientDbContextScopeIdHolder.Value = newAmbientScope._instanceId;

            // Keep track of this instance (or do nothing if we're already tracking it)
            dbContextScopeInstances.GetValue(newAmbientScope._instanceId, _ => newAmbientScope);
        }

        private static void RemoveAmbientScope()
        {
            var instanceIdentifier = ambientDbContextScopeIdHolder.Value;
            ambientDbContextScopeIdHolder.Value = null;

            if (instanceIdentifier != null)
                dbContextScopeInstances.Remove(instanceIdentifier);
        }

        internal static DbContextScope? GetAmbientScope()
        {
            var instanceIdentifier = ambientDbContextScopeIdHolder.Value;
            if (instanceIdentifier == null)
                return null;

            if (dbContextScopeInstances.TryGetValue(instanceIdentifier, out var ambientScope))
                return ambientScope;

            throw new InvalidOperationException("Found a reference to an ambient DbContextScope in the control flow " +
                                                "but no instance in the dbContextScopeInstances table.\r\n\r\n" +
                                                $"{Environment.StackTrace}");
        }
    }
}
