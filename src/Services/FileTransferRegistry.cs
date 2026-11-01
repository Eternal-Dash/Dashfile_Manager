using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DashFile.PluginApi;
namespace DashFile.Services;

/// <summary>Per-host routing. Only an unavailable-before-start result permits fallback.</summary>
public sealed class FileTransferRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _providers = new();
    private int _active;
    public FileTransferOperations Operations { get; } = new();
    public int ActiveCount => Volatile.Read(ref _active) + Operations.ActiveCount;
    public event Action<string>? Status;
    public event Action<PluginTransferProgress>? Progress;
    public IDisposable Register(string owner, IPluginFileTransferProvider provider, int priority)
    {
        var item = new Registration(this, owner, provider, priority);
        lock (_gate) _providers.Add(item);
        return item;
    }
    public async Task<bool> TryTransferAsync(PluginTransferRequest request, CancellationToken ct, IProgress<PluginTransferProgress>? progress=null)
    {
        Registration[] providers;
        lock (_gate) providers = _providers.OrderByDescending(p => p.Priority).ThenBy(p => p.Owner, StringComparer.Ordinal).ToArray();
        foreach (var p in providers)
        {
            if (!p.Provider.IsEnabled) continue;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _active);
            try
            {
                await p.Provider.TransferAsync(request, new ForwardProgress(this,progress), ct).ConfigureAwait(false);
                return true;
            }
            catch (PluginTransferUnavailableException ex) { Say("使用 ADB 兼容传输：" + ex.Message); }
            finally { Interlocked.Decrement(ref _active); }
        }
        return false;
    }
    private void Say(string text) { try { Status?.Invoke(text); } catch { } }
    private sealed class ForwardProgress(FileTransferRegistry owner,IProgress<PluginTransferProgress>? local) : IProgress<PluginTransferProgress>
    { public void Report(PluginTransferProgress value) { local?.Report(value);try { owner.Progress?.Invoke(value); } catch { } } }
    private sealed class Registration(FileTransferRegistry owner, string id, IPluginFileTransferProvider provider, int priority) : IDisposable
    {
        public string Owner { get; } = id;
        public IPluginFileTransferProvider Provider { get; } = provider;
        public int Priority { get; } = priority;
        public void Dispose() { lock (owner._gate) owner._providers.Remove(this); }
    }
}
