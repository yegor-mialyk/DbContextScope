/* 
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.DbContextScope
{
    public interface IAmbientDbContextLocator
    {
        TDbContext? Get<TDbContext>() where TDbContext : DbContext;
    }
}
