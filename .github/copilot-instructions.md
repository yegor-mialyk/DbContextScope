# DbContextScope Project Guidelines

## Overview

Entity Framework Core library implementing ambient context pattern for DbContext lifecycle management. Uses AsyncLocal for async-safe scope tracking and ConditionalWeakTable for weak-reference instance storage.

## Code Style

**File Structure**: All `.cs` files start with copyright header (Mehdi El Gueddari 2014, Yegor Mialyk 2020-2026, MIT License). See [DbContextScope.cs](../DbContextScope.cs#L1-L8) for template.

**Modern C# Requirements**:
- Nullable reference types enabled: use `?` annotations, `is null/not null` pattern matching
- File-scoped namespaces: `namespace EntityFrameworkCore.DbContextScope;`
- Target-typed new: `new()` instead of `new Dictionary<Type, DbContext>()`
- Throw helpers: `ObjectDisposedException.ThrowIf(_disposed, nameof(Class))`
- Sealed implementation classes, explicit interfaces

**Naming**: Private fields `_camelCase`, explicit `this.` not used

## Architecture

**Core Components**:
- [DbContextScope.cs](../DbContextScope.cs): Ambient scope manager with `AsyncLocal<object?>` + `ConditionalWeakTable` pattern
- [DbContextCollection.cs](../DbContextCollection.cs): Lazy DbContext initialization (one instance per type per scope), transaction coordination
- [AmbientDbContextLocator.cs](../AmbientDbContextLocator.cs): Locator for repository pattern access
- [DbContextScopeFactory.cs](../DbContextScopeFactory.cs): Factory with `Create()`, `CreateReadOnly()`, `HideContext()` methods

**Key Mechanisms**:
- Scope nesting via `_parentScope` field, `_nested` flag determines if DbContexts collection is shared
- Dispose order enforcement: throws if parent disposed first (parallel execution bug indicator)
- Transaction management: only when `IsolationLevel != Unspecified`, coordinated commit/rollback

**Ambient Context Implementation**:
```csharp
// AsyncLocal flows across async/await boundaries
private static readonly AsyncLocal<object?> ambientDbContextScopeIdHolder = new();

// ConditionalWeakTable prevents memory leaks (weak references)
private static readonly ConditionalWeakTable<object, DbContextScope> dbContextScopeInstances = new();
```
Each scope creates unique `_instanceId` object. AsyncLocal stores ID (flows with ExecutionContext), ConditionalWeakTable stores actual instance (GC-friendly). This dual approach ensures thread-safety and proper async flow without memory leaks.

## Critical Patterns

**Async/Await**: Always use `.ConfigureAwait(false)` on all awaits (see [DbContextScope.cs](../DbContextScope.cs#L79))

**Error Preservation**: Use `ExceptionDispatchInfo.Capture(e)` and `.Throw()` to preserve stack traces when collecting multiple errors (see [DbContextCollection.cs](../DbContextCollection.cs#L88-L96))

**Detailed Error Messages**: Include `Environment.StackTrace` in programming error exceptions for debugging context (see [DbContextScope.cs](../DbContextScope.cs#L118-L130))

**Logging**: Inject `ILoggerFactory`, create typed loggers: `_logger = loggerFactory.CreateLogger<ClassName>()`

**Read-Only Mode**: Set `DbContext.ChangeTracker.AutoDetectChangesEnabled = false` and `QueryTrackingBehavior.NoTracking` (see [DbContextCollection.cs](../DbContextCollection.cs#L62-L64))

**Error Handling Examples**:
```csharp
// For disposal checks - throws with proper type name
ObjectDisposedException.ThrowIf(_disposed, nameof(DbContextScope));

// For programming errors - include Environment.StackTrace
throw new InvalidOperationException(
    $"""
    PROGRAMMING ERROR - description of what went wrong...
    {Environment.StackTrace}
    """);

// For collecting multiple errors - preserve stack traces
ExceptionDispatchInfo? lastError = null;
foreach (var item in collection)
{
    try { /* operation */ }
    catch (Exception e) { lastError = ExceptionDispatchInfo.Capture(e); }
}
if (lastError is not null) lastError.Throw();
```

## Dependency Injection Setup

**ASP.NET Core Registration**:
```csharp
// In Program.cs or Startup.cs
builder.Services.AddSingleton<IDbContextScopeFactory, DbContextScopeFactory>();
builder.Services.AddSingleton<IAmbientDbContextLocator, AmbientDbContextLocator>();
builder.Services.AddSingleton<IDbContextFactory, YourCustomFactory>(); // Optional
```

**Repository Pattern**:
```csharp
public class UserRepository
{
    private readonly IAmbientDbContextLocator _locator;

    public UserRepository(IAmbientDbContextLocator locator) => _locator = locator;

    public User Get(Guid id) => _locator.Get<MyDbContext>().Set<User>().Find(id);
}
```

**Service Pattern**:
```csharp
public class UserService
{
    private readonly IDbContextScopeFactory _scopeFactory;
    private readonly IUserRepository _userRepo;

    public void MarkPremium(Guid userId)
    {
        using var scope = _scopeFactory.Create();
        var user = _userRepo.Get(userId);
        user.IsPremium = true;
        scope.SaveChanges();
    }
}
```

## Build and Test

**Build**: `dotnet build` - targets .NET 10.0, depends on EF Core 10.0 (`Microsoft.EntityFrameworkCore.Relational`)

**Project Config**: `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `<AnalysisLevel>latest</AnalysisLevel>` in [EntityFrameworkCore.DbContextScope.csproj](../EntityFrameworkCore.DbContextScope.csproj)

## Anti-Patterns to Avoid

❌ Don't use ambient scope in parallel tasks without `HideContext()` - causes DbContext thread-safety violations
❌ Don't call `SaveChanges()` multiple times on same scope - enforced by `_completed` flag
❌ Don't dispose scopes out of order - enforced with exception
❌ Don't use `.ConfigureAwait(true)` or omit ConfigureAwait - breaks library context independence
