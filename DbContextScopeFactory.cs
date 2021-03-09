/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System;
using System.Data;

namespace EntityFrameworkCore.DbContextScope
{
    public class DbContextScopeFactory : IDbContextScopeFactory
    {
        private readonly IDbContextFactory? _dbContextFactory;

        public DbContextScopeFactory(IDbContextFactory? dbContextFactory = null)
        {
            _dbContextFactory = dbContextFactory;
        }

        public IDbContextScope Create(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting, bool readOnly = false)
        {
            return new DbContextScope(joiningOption, readOnly, null, _dbContextFactory);
        }

        public IDbContextScope CreateWithTransaction(IsolationLevel isolationLevel)
        {
            return new DbContextScope(DbContextScopeOption.ForceCreateNew, false, isolationLevel, _dbContextFactory);
        }

        public IDisposable HideContext()
        {
            return new DbContextScope(DbContextScopeOption.Suppress);
        }
    }
}
