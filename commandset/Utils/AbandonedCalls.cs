using System.Collections.Concurrent;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Handlers whose caller stopped waiting. A handler queued behind a dialog or a long
    /// operation still runs after its command has timed out; by then nobody reads its result,
    /// so it must not change the model.
    /// </summary>
    public static class AbandonedCalls
    {
        private static readonly ConcurrentDictionary<object, byte> Handlers = new ConcurrentDictionary<object, byte>();

        public static void Mark(object handler) => Handlers[handler] = 0;

        public static void Clear(object handler) => Handlers.TryRemove(handler, out _);

        public static bool IsAbandoned(object handler) => handler != null && Handlers.ContainsKey(handler);
    }
}
