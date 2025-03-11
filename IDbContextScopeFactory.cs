//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2025, Yegor Mialyk. All Rights Reserved.
//
// Licensed under the MIT License. See the LICENSE file for details.
//

using System.Data;

namespace EntityFrameworkCore.DbContextScope;

public interface IDbContextScopeFactory
{
    IDbContextScope Create(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting);

    IDbContextScope Create(IsolationLevel isolationLevel);

    IDisposable CreateReadOnly(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting);

    IDisposable HideContext();
}
