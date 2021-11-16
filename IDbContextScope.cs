/* 
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

namespace EntityFrameworkCore.DbContextScope;

public interface IDbContextScope : IDisposable
{
    int SaveChanges();
    Task<int> SaveChangesAsync(CancellationToken cancelToken);
}
