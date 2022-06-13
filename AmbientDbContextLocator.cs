/* 
 * Copyright (C) 2014 Mehdi El Gueddari
 * http://mehdi.me
 *
 * This software may be modified and distributed under the terms
 * of the MIT license.  See the LICENSE file for details.
 */

using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.DbContextScope;

public class AmbientDbContextLocator : IAmbientDbContextLocator
{
    public TDbContext Get<TDbContext>() where TDbContext : DbContext
    {
        var ambientDbContextScope = DbContextScope.GetAmbientScope();

        if (ambientDbContextScope is null)
            throw new InvalidOperationException(
                "No ambient DbContext scope found. The method has been called outside of the DbContextScope.");

        return ambientDbContextScope.DbContexts.Get<TDbContext>();
    }
}
