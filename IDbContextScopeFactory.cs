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
    public interface IDbContextScopeFactory
    {
        IDbContextScope Create(DbContextScopeOption joiningOption = DbContextScopeOption.JoinExisting, bool readOnly = false);
        IDbContextScope Create(IsolationLevel isolationLevel);
        IDisposable HideContext();
    }
}
