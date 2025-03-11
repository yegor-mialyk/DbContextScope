//
// Entity Framework Core DbContext Scope
//
// Copyright (C) 2014 Mehdi El Gueddari (http://mehdi.me)
// Copyright (C) 2020-2025, Yegor Mialyk. All Rights Reserved.
//
// Licensed under the MIT License. See the LICENSE file for details.
//

using Microsoft.EntityFrameworkCore;

namespace EntityFrameworkCore.DbContextScope;

public sealed class AmbientDbContextLocator : IAmbientDbContextLocator
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
