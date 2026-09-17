# DbContextScope Project Guidelines

## Overview

Entity Framework Core library implementing the ambient context pattern for `DbContext` lifecycle management. The implementation tracks ambient scope identity in `AsyncLocal<object?>` and resolves scope instances through a `ConditionalWeakTable<object, DbContextScope>`.

## Code Style

**File structure**: All `.cs` files start with the project copyright header (Mehdi El Gueddari 2014, Yegor Mialyk 2020-2026, MIT License). Use existing files as the template.

**Modern C# requirements**:
- Nullable reference types enabled: use `?` annotations, `is null/not null` pattern matching
- File-scoped namespaces: `namespace EntityFrameworkCore.DbContextScope;`
- Target-typed `new()` where it improves clarity
- Throw helpers: `ObjectDisposedException.ThrowIf(_disposed, nameof(Class))`
- Sealed implementation classes with direct public interface members (no explicit interface member implementations)

**Naming**: Private fields use `_camelCase`; avoid unnecessary `this.` qualifiers.

## Architecture

**Core components**:
- `DbContextScope.cs`: Ambient scope manager with `AsyncLocal<object?>` + `ConditionalWeakTable` pattern
- `DbContextCollection.cs`: Lazy `DbContext` initialization (one instance per type per scope), transaction coordination, commit/rollback/disposal orchestration
- `AmbientDbContextLocator.cs`: Locator for repository pattern access
- `DbContextScopeFactory.cs`: Factory with `Create()`, `Create(IsolationLevel)`, `CreateReadOnly()`, and `HideContext()`
- `DbContextScopeOptions.cs`: Scope behavior options enum (`JoinExisting`, `CreateNew`, `Suppress`)
- `IDbContextFactory.cs`: Optional abstraction to create `DbContext` instances; fallback path is `Activator.CreateInstance<TDbContext>()`

**Key mechanisms**:
- `DbContextScopeOption` enum: `JoinExisting` (default), `CreateNew`, `Suppress`
- Scope nesting via `_parentScope`; `_nested` is enabled only when joining an existing scope and read-only mode matches parent/child
- Requesting a non-default `IsolationLevel` forces `DbContextScopeOption.CreateNew`, even if `JoinExisting` was requested
- Dispose order enforcement: throws if parent disposed first (parallel execution bug indicator)
- Transaction management: transactions are created per initialized context only when `IsolationLevel != Unspecified`
- Read-only mode behavior: `AutoDetectChangesEnabled = false` and `QueryTrackingBehavior = NoTracking`

**Ambient Context Implementation**:
```csharp
// AsyncLocal flows across async/await boundaries
private static readonly AsyncLocal<object?> ambientDbContextScopeIdHolder = new();

// ConditionalWeakTable prevents memory leaks (weak references)
private static readonly ConditionalWeakTable<object, DbContextScope> dbContextScopeInstances = new();
```
Each scope creates unique `_instanceId` object. AsyncLocal stores ID (flows with ExecutionContext), ConditionalWeakTable stores actual instance (GC-friendly). This dual approach ensures thread-safety and proper async flow without memory leaks.

## Critical Patterns

**Async/Await**: Always use `.ConfigureAwait(false)` on all awaits (see `DbContextScope.cs`)

**Error preservation**: Use `ExceptionDispatchInfo.Capture(e)` and `.Throw()` to preserve stack traces when collecting multiple errors (see `DbContextCollection.cs`)

**Detailed error messages**: Include `Environment.StackTrace` in programming error exceptions for debugging context (see `DbContextScope.cs`)

**Logging**: Inject `ILoggerFactory` and create typed loggers with `CreateLogger<T>()`.

**Commit semantics**:
- `IDbContextScope.SaveChanges()` and `SaveChangesAsync()` can be called only once per scope
- Nested scopes do not commit; only the top-level non-nested scope commits
- `DbContextCollection.Commit*()` can be called only once per collection

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

**Build**: `dotnet build`

**Project configuration** (`EntityFrameworkCore.DbContextScope.csproj`):
- `TargetFramework`: `net10.0`
- `Nullable`: `enable`
- `LangVersion`: `latest`
- `ImplicitUsings`: `enable`
- `AnalysisLevel`: `latest`
- EF Core package: `Microsoft.EntityFrameworkCore.Relational` (currently `10.0.12`)

## Anti-Patterns to Avoid

Don't use ambient scope in parallel tasks without `HideContext()` - causes `DbContext` thread-safety violations
Don't call `SaveChanges()`/`SaveChangesAsync()` multiple times on the same scope
Don't dispose scopes out of creation order
Don't start transactional scopes with `JoinExisting` assumptions when explicitly passing `IsolationLevel` (constructor forces `CreateNew`)
Don't omit `.ConfigureAwait(false)` on library awaits
