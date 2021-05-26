/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System;
using System.Data;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EntityFrameworkCore.DbContextScope
{
    public class DbContextScope : IDbContextScope
    {
        private bool _completed;
        private bool _disposed;
        private readonly bool _nested;
        private readonly DbContextScope? _parentScope;
        private readonly DbContextScopeOption _joiningOption;
        private readonly bool _readOnly;

        public DbContextScope(DbContextScopeOption joiningOption, bool readOnly = false,
            IsolationLevel isolationLevel = IsolationLevel.Unspecified,
            IDbContextFactory? dbContextFactory = null)
        {
            if (isolationLevel != IsolationLevel.Unspecified)
                joiningOption = DbContextScopeOption.CreateNew;

            _joiningOption = joiningOption;
            _readOnly = readOnly;

            _parentScope = GetAmbientScope();

            if (joiningOption == DbContextScopeOption.Suppress)
            {
                ambientDbContextScopeIdentifier.Value = null;
                return;
            }

            if (_parentScope != null && joiningOption == DbContextScopeOption.JoinExisting)
            {
                if (_parentScope._readOnly && !_readOnly)
                    throw new InvalidOperationException(
                        "Cannot nest a read/write DbContextScope within a read-only DbContextScope.");

                _nested = true;
                DbContexts = _parentScope.DbContexts;
            }
            else
                DbContexts = new DbContextCollection(_readOnly, isolationLevel, dbContextFactory);

            SetAmbientScope(this);
        }

        public DbContextCollection DbContexts { get; } = null!;

        public int SaveChanges()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextScope));
            if (_completed)
                throw new InvalidOperationException(
                    "You cannot call SaveChanges() more than once on a DbContextScope.");

            // Only save changes if we're not a nested scope. Otherwise, let the top-level scope
            // decide when the changes should be saved.
            var c = 0;
            if (!_nested)
                c = DbContexts!.Commit();

            _completed = true;

            return c;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancelToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextScope));
            if (_completed)
                throw new InvalidOperationException(
                    "You cannot call SaveChanges() more than once on a DbContextScope.");

            var c = 0;
            if (!_nested)
                c = await DbContexts!.CommitAsync(cancelToken).ConfigureAwait(false);

            _completed = true;
            return c;
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

                return;
            }

            if (!_nested)
            {
                if (!_completed)
                {
                    try
                    {
                        if (_readOnly)
                            DbContexts.Commit();
                        else
                            // Disposing a read/write scope before having called its SaveChanges() method
                            // indicates that something went wrong and that all changes should be rolled-back.
                            DbContexts.Rollback();
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }

                    _completed = true;
                }

                DbContexts.Dispose();
            }

            // Pop ourselves from the ambient scope stack
            var currentAmbientScope = GetAmbientScope();
            if (currentAmbientScope != this)
                throw new InvalidOperationException("DbContextScope instances must be disposed of in the order in which they were created.");

            RemoveAmbientScope();

            if (_parentScope != null)
            {
                if (_parentScope._disposed)
                /*
                     * If our parent scope has been disposed before us, it can only mean one thing:
                     * someone started a parallel flow of execution and forgot to suppress the
                     * ambient context before doing so. And we've been created in that parallel flow.
                     *
                     * Since the CallContext flows through all async points, the ambient scope in the
                     * main flow of execution ended up becoming the ambient scope in this parallel flow
                     * of execution as well. So when we were created, we captured it as our "parent scope".
                     *
                     * The main flow of execution then completed while our flow was still ongoing. When
                     * the main flow of execution completed, the ambient scope there (which we think is our
                     * parent scope) got disposed of as it should.
                     *
                     * So here we are: our parent scope isn't actually our parent scope. It was the ambient
                     * scope in the main flow of execution from which we branched off. We should never have seen
                     * it. Whoever wrote the code that created this parallel task should have suppressed
                     * the ambient context before creating the task - that way we wouldn't have captured
                     * this bogus parent scope.
                     *
                     * While this is definitely a programming error, it's not worth throwing here. We can only
                     * be in one of two scenario:
                     *
                     * - If the developer who created the parallel task was mindful to force the creation of
                     * a new scope in the parallel task (with IDbContextScopeFactory.CreateNew() instead of
                     * JoinOrCreate()) then no harm has been done. We haven't tried to access the same DbContext
                     * instance from multiple threads.
                     *
                     * - If this was not the case, they probably already got an exception complaining about the same
                     * DbContext or ObjectContext being accessed from multiple threads simultaneously (or a related
                     * error like multiple active result sets on a DataReader, which is caused by attempting to execute
                     * several queries in parallel on the same DbContext instance). So the code has already blow up.
                     *
                     * So just record a warning here. Hopefully someone will see it and will fix the code.
                     */

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

        private static readonly AsyncLocal<object?> ambientDbContextScopeIdentifier = new();

        private static readonly ConditionalWeakTable<object, DbContextScope> dbContextScopeInstances = new();

        private readonly object _instanceIdentifier = new();

        private static void SetAmbientScope(DbContextScope newAmbientScope)
        {
            if (newAmbientScope == null)
                throw new ArgumentNullException(nameof(newAmbientScope));

            var current = ambientDbContextScopeIdentifier.Value;

            if (current == newAmbientScope._instanceIdentifier)
                return;

            // Store the new scope's instance identifier in the CallContext, making it the ambient scope
            ambientDbContextScopeIdentifier.Value = newAmbientScope._instanceIdentifier;

            // Keep track of this instance (or do nothing if we're already tracking it)
            dbContextScopeInstances.GetValue(newAmbientScope._instanceIdentifier, _ => newAmbientScope);
        }

        private static void RemoveAmbientScope()
        {
            var instanceIdentifier = ambientDbContextScopeIdentifier.Value;
            ambientDbContextScopeIdentifier.Value = null;

            if (instanceIdentifier != null)
                dbContextScopeInstances.Remove(instanceIdentifier);
        }

        internal static DbContextScope? GetAmbientScope()
        {
            var instanceIdentifier = ambientDbContextScopeIdentifier.Value;
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
