//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2025, Yegor Mialyk. All Rights Reserved.
//
// Licensed under the MIT License. See the LICENSE file for details.
//

using System.Data;
using Microsoft.Extensions.Logging;

namespace EntityFrameworkCore.DbContextScope;

public sealed class DbContextScopeFactory : IDbContextScopeFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDbContextFactory? _dbContextFactory;

    public DbContextScopeFactory(ILoggerFactory loggerFactory, IDbContextFactory? dbContextFactory = null)
    {
        _loggerFactory = loggerFactory;
        _dbContextFactory = dbContextFactory;
    }

    public IDbContextScope Create(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting)
    {
        return new DbContextScope(_loggerFactory, joiningOption, false, IsolationLevel.Unspecified, _dbContextFactory);
    }

    public IDbContextScope Create(IsolationLevel isolationLevel)
    {
        return new DbContextScope(_loggerFactory, DbContextScopeOption.CreateNew, false, isolationLevel, _dbContextFactory);
    }

    public IDisposable CreateReadOnly(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting)
    {
        return new DbContextScope(_loggerFactory, joiningOption, true, IsolationLevel.Unspecified, _dbContextFactory);
    }

    public IDisposable HideContext()
    {
        return new DbContextScope(_loggerFactory, DbContextScopeOption.Suppress);
    }
}
