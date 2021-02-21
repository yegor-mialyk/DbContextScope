using System.Collections.Concurrent;
using System.Threading;

namespace EntityFrameworkCore.DbContextScope.Common
{
    /// <summary>
    ///     http://www.cazzulino.com/callcontext-netstandard-netcore.html
    /// </summary>
    internal static class CallContext
    {
        private static readonly ConcurrentDictionary<string, AsyncLocal<object>> context = new();

        public static void SetData(string name, object data)
        {
            context.GetOrAdd(name, _ => new AsyncLocal<object>()).Value = data;
        }

        public static object GetData(string name)
        {
            return context.TryGetValue(name, out var data) ? data.Value : null;
        }
    }
}