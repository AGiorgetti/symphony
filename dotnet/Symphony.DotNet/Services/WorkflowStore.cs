using System.Security.Cryptography;
using System.Text;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed record WorkflowStoreSnapshot(
    WorkflowDocument Workflow,
    string? LastReloadError,
    DateTimeOffset LoadedAtUtc,
    DateTimeOffset? LastFailedReloadAtUtc);

internal sealed class WorkflowStore(CliOptions options, ILogger<WorkflowStore> logger) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly WorkflowLoader _loader = new();
    private readonly string _workflowPath = options.WorkflowPath;

    private FileSystemWatcher? _watcher;
    private WorkflowStoreSnapshot? _snapshot;
    private WorkflowStamp? _stamp;
    private bool _dirty = true;

    public WorkflowStoreSnapshot LoadInitial()
    {
        lock (_lock)
        {
            EnsureWatcherLocked();
            if (_snapshot is null)
            {
                ReloadLocked(force: true);
            }

            return _snapshot ?? throw new InvalidOperationException("workflow_not_loaded");
        }
    }

    public WorkflowStoreSnapshot Current()
    {
        lock (_lock)
        {
            EnsureWatcherLocked();
            if (_snapshot is null)
            {
                ReloadLocked(force: true);
            }
            else
            {
                ReloadLocked(force: false);
            }

            return _snapshot ?? throw new InvalidOperationException("workflow_not_loaded");
        }
    }

    public WorkflowStoreSnapshot ForceReload()
    {
        lock (_lock)
        {
            EnsureWatcherLocked();
            ReloadLocked(force: true);
            return _snapshot ?? throw new InvalidOperationException("workflow_not_loaded");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private void ReloadLocked(bool force)
    {
        if (!force && !_dirty && _snapshot is not null && !StampChangedLocked())
        {
            return;
        }

        try
        {
            var workflow = _loader.Load(_workflowPath);
            var stamp = ReadStamp(_workflowPath);
            var now = DateTimeOffset.UtcNow;

            _snapshot = new WorkflowStoreSnapshot(
                workflow,
                LastReloadError: null,
                LoadedAtUtc: now,
                LastFailedReloadAtUtc: null);
            _stamp = stamp;
            _dirty = false;
        }
        catch (Exception ex) when (_snapshot is not null)
        {
            logger.LogError(ex, "action=workflow_reload outcome=failed workflow_path={WorkflowPath}; keeping last known good configuration", _workflowPath);
            _snapshot = _snapshot with
            {
                LastReloadError = ex.Message,
                LastFailedReloadAtUtc = DateTimeOffset.UtcNow
            };
            _dirty = false;
        }
    }

    private bool StampChangedLocked()
    {
        try
        {
            var current = ReadStamp(_workflowPath);
            return !Equals(current, _stamp);
        }
        catch
        {
            return true;
        }
    }

    private void EnsureWatcherLocked()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_workflowPath);
        var fileName = Path.GetFileName(_workflowPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnWorkflowChanged;
        _watcher.Created += OnWorkflowChanged;
        _watcher.Deleted += OnWorkflowChanged;
        _watcher.Renamed += OnWorkflowChanged;
    }

    private void OnWorkflowChanged(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            _dirty = true;
        }
    }

    private static WorkflowStamp ReadStamp(string workflowPath)
    {
        var info = new FileInfo(workflowPath);
        if (!info.Exists)
        {
            throw new InvalidOperationException($"missing_workflow_file: {workflowPath}");
        }

        var content = File.ReadAllText(workflowPath);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var hash = Convert.ToHexString(hashBytes);
        return new WorkflowStamp(info.LastWriteTimeUtc.Ticks, info.Length, hash);
    }

    private sealed record WorkflowStamp(long LastWriteUtcTicks, long Length, string ContentHash);
}
