using System;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.PluginApi;

public enum PluginTransferDirection { Download, Upload }
public sealed record PluginTransferRequest(string Serial, bool UseRoot, string AdbPath,
    PluginTransferDirection Direction, string RemotePath, string LocalPath);
public sealed record PluginTransferProgress(long CompletedBytes, long TotalBytes, string Channels, string FileName, string Phase = "传输中");
/// <summary>Thrown only before accepting a transfer; the host may use its normal ADB backend.</summary>
public sealed class PluginTransferUnavailableException : Exception
{
    public PluginTransferUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
public interface IPluginFileTransferProvider
{
    string DisplayName { get; }
    bool IsEnabled { get; }
    Task TransferAsync(PluginTransferRequest request, IProgress<PluginTransferProgress>? progress, CancellationToken cancellationToken);
}
/// <summary>Optional API v3. Registering a provider routes ordinary host uploads/downloads through it.</summary>
public interface IPluginFileTransferHost
{
    string AdbExecutablePath { get; }
    IDisposable RegisterFileTransferProvider(IPluginFileTransferProvider provider, int priority = 100);
}
