using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Hnpb;

/// <summary>All calls run on the game thread. Return DTOs, not live game objects. Dispose registration on mod unload.</summary>
public interface IBridgeAdapter
{
    string Id { get; }
    object Describe();
    object Observe(object? os);
    object Invoke(object? os, string operation, Dictionary<string, object> arguments);
}

public static class BridgeAdapters
{
    private sealed class Registration : IDisposable
    {
        internal readonly IBridgeAdapter Adapter;
        internal readonly Assembly Owner;
        internal Registration(IBridgeAdapter adapter, Assembly owner) { Adapter = adapter; Owner = owner; }
        public void Dispose() { lock (sync) { if (entries.TryGetValue(Adapter.Id, out var entry) && ReferenceEquals(this, entry)) entries.Remove(Adapter.Id); } }
    }
    private static readonly object sync = new();
    private static readonly Dictionary<string, Registration> entries = new(StringComparer.Ordinal);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable Register(IBridgeAdapter adapter)
    {
        if (adapter == null || string.IsNullOrWhiteSpace(adapter.Id) || !adapter.Id.Contains("."))
            throw new ArgumentException("Adapter needs a namespaced Id.");
        lock (sync)
        {
            if (entries.ContainsKey(adapter.Id)) throw new ArgumentException("Adapter Id already registered.");
            var registration = new Registration(adapter, Assembly.GetCallingAssembly());
            entries.Add(adapter.Id, registration); return registration;
        }
    }
    internal static object[] Describe()
    {
        Registration[] snapshot; lock (sync) snapshot = entries.Values.ToArray();
        return snapshot.Select(x => (object)new { x.Adapter.Id, Owner = x.Owner.GetName().Name, Schema = x.Adapter.Describe() }).ToArray();
    }
    internal static IBridgeAdapter Get(string id)
    { lock (sync) return entries.TryGetValue(id, out var entry) ? entry.Adapter : throw new ArgumentException("Adapter not registered: " + id); }
    internal static void Clear() { lock (sync) entries.Clear(); }
}
