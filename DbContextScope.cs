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
                asyncLocal.Value = null;
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

        public DbContextCollection? DbContexts { get; }

        public int SaveChanges()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DbContextScope));
            if (_completed)
                throw new InvalidOperationException(
                    "You cannot call SaveChanges() more than once on a DbContextScope. A DbContextScope is meant to encapsulate a business transaction: create the scope at the start of the business transaction and then call SaveChanges() at the end. Calling SaveChanges() mid-way through a business transaction doesn't make sense and most likely mean that you should refactor your service method into two separate service method that each create their own DbContextScope and each implement a single business transaction.");

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

            // Only save changes if we're not a nested scope. Otherwise, let the top-level scope 
            // decide when the changes should be saved.
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
                            DbContexts?.Commit();
                        else
                            // Disposing a read/write scope before having called its SaveChanges() method
                            // indicates that something went wrong and that all changes should be rolled-back.
                            DbContexts?.Rollback();
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }

                    _completed = true;
                }

                DbContexts?.Dispose();
            }

            // Pop ourselves from the ambient scope stack
            var currentAmbientScope = GetAmbientScope();
            if (currentAmbientScope != this)
                throw new InvalidOperationException("DbContextScope instances must be disposed of in the order in which they were created.");

            RemoveAmbientScope();

            if (_parentScope != null)
            {
                if (_parentScope._disposed)
                {
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
                }

                SetAmbientScope(_parentScope);
            }

            _disposed = true;
        }

        /*
         * This is where all the magic happens. And there is not much of it.
         * 
         * This implementation is inspired by the source code of the
         * TransactionScope class in .NET 4.5.1 (the TransactionScope class
         * is prior versions of the .NET Fx didn't have support for async
         * operations).
         * 
         * In order to understand this, you'll need to be familiar with the
         * concept of async points. You'll also need to be familiar with the
         * ExecutionContext and CallContext and understand how and why they 
         * flow through async points. Stephen Toub has written an
         * excellent blog post about this - it's a highly recommended read:
         * http://blogs.msdn.com/b/pfxteam/archive/2012/06/15/executioncontext-vs-synchronizationcontext.aspx
         * 
         * Overview: 
         * 
         * We want our DbContextScope instances to be ambient within 
         * the context of a logical flow of execution. This flow may be 
         * synchronous or it may be asynchronous.
         * 
         * If we only wanted to support the synchronous flow scenario, 
         * we could just store our DbContextScope instances in a ThreadStatic 
         * variable. That's the "traditional" (i.e. pre-async) way of implementing
         * an ambient context in .NET. You can see an example implementation of 
         * a TheadStatic-based ambient DbContext here: http://coding.abel.nu/2012/10/make-the-dbcontext-ambient-with-unitofworkscope/ 
         * 
         * But that would be hugely limiting as it would prevent us from being
         * able to use the new async features added to Entity Framework
         * in EF6 and .NET 4.5.
         * 
         * So we need a storage place for our DbContextScope instances 
         * that can flow through async points so that the ambient context is still 
         * available after an await (or any other async point). And this is exactly 
         * what CallContext is for.
         * 
         * There are however two issues with storing our DbContextScope instances 
         * in the CallContext:
         * 
         * 1) Items stored in the CallContext should be serializable. That's because
         * the CallContext flows not just through async points but also through app domain 
         * boundaries. I.e. if you make a remoting call into another app domain, the
         * CallContext will flow through this call (which will require all the values it
         * stores to get serialized) and get restored in the other app domain.
         * 
         * In our case, our DbContextScope instances aren't serializable. And in any case,
         * we most definitely don't want them to be flown across app domains. So we'll
         * use the trick used by the TransactionScope class to work around this issue.
         * Instead of storing our DbContextScope instances themselves in the CallContext,
         * we'll just generate a unique key for each instance and only store that key in 
         * the CallContext. We'll then store the actual DbContextScope instances in a static
         * Dictionary against their key. 
         * 
         * That way, if an app domain boundary is crossed, the keys will be flown across
         * but not the DbContextScope instances since a static variable is stored at the 
         * app domain level. The code executing in the other app domain won't see the ambient
         * DbContextScope created in the first app domain and will therefore be able to create
         * their own ambient DbContextScope if necessary.
         * 
         * 2) The CallContext is flow through *all* async points. This means that if someone
         * decides to create multiple threads within the scope of a DbContextScope, our ambient scope
         * will flow through all the threads. Which means that all the threads will see that single 
         * DbContextScope instance as being their ambient DbContext. So clients need to be 
         * careful to always suppress the ambient context before kicking off a parallel operation
         * to avoid our DbContext instances from being accessed from multiple threads.
         * 
         */

        private static readonly AsyncLocal<object?> asyncLocal = new();

        private static readonly ConditionalWeakTable<object, DbContextScope> dbContextScopeInstances = new();

        private readonly object _instanceIdentifier = new();

        internal static void SetAmbientScope(DbContextScope newAmbientScope)
        {
            if (newAmbientScope == null)
                throw new ArgumentNullException(nameof(newAmbientScope));

            var current = asyncLocal.Value;

            if (current == newAmbientScope._instanceIdentifier)
                return;

            // Store the new scope's instance identifier in the CallContext, making it the ambient scope
            asyncLocal.Value = newAmbientScope._instanceIdentifier;

            // Keep track of this instance (or do nothing if we're already tracking it)
            dbContextScopeInstances.GetValue(newAmbientScope._instanceIdentifier, _ => newAmbientScope);
        }

        private static void RemoveAmbientScope()
        {
            var instanceIdentifier = asyncLocal.Value;
            asyncLocal.Value = null;

            if (instanceIdentifier != null)
                dbContextScopeInstances.Remove(instanceIdentifier);
        }

        internal static DbContextScope? GetAmbientScope()
        {
            var instanceIdentifier = asyncLocal.Value;
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
