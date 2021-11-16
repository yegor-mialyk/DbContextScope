/*
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using System.Data;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.DbContextScope;

public class DbContextScopeFactory : IDbContextScopeFactory
{
    private readonly ILogger<DbContextScope> _logger;
    private readonly IDbContextFactory? _dbContextFactory;

    public DbContextScopeFactory(ILogger<DbContextScope> logger, IDbContextFactory? dbContextFactory = null)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    public IDbContextScope Create(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting)
    {
        return new DbContextScope(_logger, joiningOption, false, IsolationLevel.Unspecified, _dbContextFactory);
    }

    public IDbContextScope Create(IsolationLevel isolationLevel)
    {
        return new DbContextScope(_logger, DbContextScopeOption.CreateNew, false, isolationLevel, _dbContextFactory);
    }

    public IDisposable CreateReadOnly(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting)
    {
        return new DbContextScope(_logger, joiningOption, true, IsolationLevel.Unspecified, _dbContextFactory);
    }

    public IDisposable HideContext()
    {
        return new DbContextScope(_logger, DbContextScopeOption.Suppress);
    }
}
