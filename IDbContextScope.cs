//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2025, Yegor Mialyk. All Rights Reserved.
//
// Licensed under the MIT License. See the LICENSE file for details.
//

namespace EntityFrameworkCore.DbContextScope;

public interface IDbContextScope : IDisposable
{
    int SaveChanges();
    Task<int> SaveChangesAsync(CancellationToken cancelToken);
}
