using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace GeoVisionPro.Application.Services;

public sealed class MessageLog : IMessageLog
{
    private readonly List<LogMessage> _messages = [];
    private readonly ILogger<MessageLog>? _logger;
    private readonly object _sync = new();
    public event EventHandler<LogMessage>? MessageLogged;

    public MessageLog(ILogger<MessageLog>? logger = null) => _logger = logger;

    public IReadOnlyList<LogMessage> Messages { get { lock (_sync) return _messages.ToList(); } }

    public void Write(MessageLevel level, string source, string text)
    {
        var m = new LogMessage(DateTime.Now, level, source, text);
        lock (_sync)
        {
            _messages.Add(m);
            if (_messages.Count > 20000) _messages.RemoveRange(0, 5000);
        }
        _logger?.Log(level switch { MessageLevel.Error => LogLevel.Error, MessageLevel.Warning => LogLevel.Warning, _ => LogLevel.Information }, "{Source}: {Text}", source, text);
        MessageLogged?.Invoke(this, m);
    }
}

/// <summary>Bounded undo/redo stacks of <see cref="IUndoableCommand"/>.</summary>
public sealed class UndoRedoManager
{
    private readonly Stack<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();
    public int Capacity { get; set; } = 200;
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>Records an action that has already been performed.</summary>
    public void Push(IUndoableCommand cmd)
    {
        _undo.Push(cmd);
        _redo.Clear();
        if (_undo.Count > Capacity)
        {
            var keep = _undo.Take(Capacity).Reverse().ToList();
            _undo.Clear();
            foreach (var c in keep) _undo.Push(c);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo() { if (!CanUndo) return; var c = _undo.Pop(); c.Undo(); _redo.Push(c); Changed?.Invoke(this, EventArgs.Empty); }
    public void Redo() { if (!CanRedo) return; var c = _redo.Pop(); c.Redo(); _undo.Push(c); Changed?.Invoke(this, EventArgs.Empty); }
    public void Clear() { _undo.Clear(); _redo.Clear(); Changed?.Invoke(this, EventArgs.Empty); }
}

/// <summary>Generic undoable command from delegates.</summary>
public sealed class DelegateCommand(string description, Action undo, Action redo) : IUndoableCommand
{
    public string Description { get; } = description;
    public void Undo() => undo();
    public void Redo() => redo();
}

public enum BackgroundTaskStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>A tracked background operation with progress and cancellation.</summary>
public sealed class BackgroundTask
{
    private readonly CancellationTokenSource _cts = new();
    public Guid Id { get; } = Guid.NewGuid();
    public required string Name { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Finished { get; internal set; }
    public double Progress { get; internal set; }
    public string Message { get; internal set; } = "";
    public BackgroundTaskStatus Status { get; internal set; } = BackgroundTaskStatus.Queued;
    public Exception? Error { get; internal set; }
    public Task? Task { get; internal set; }
    public CancellationToken Token => _cts.Token;
    public void Cancel() => _cts.Cancel();
    public TimeSpan Elapsed => (Finished ?? DateTime.Now) - Started;
}

/// <summary>Runs long operations off the UI thread, reports progress, supports cancellation and logs outcomes.</summary>
public sealed class BackgroundTaskManager
{
    private readonly IMessageLog _log;
    private readonly List<BackgroundTask> _tasks = [];
    private readonly object _sync = new();
    public event EventHandler<BackgroundTask>? TaskChanged;

    public BackgroundTaskManager(IMessageLog log) => _log = log;

    public IReadOnlyList<BackgroundTask> Tasks { get { lock (_sync) return _tasks.ToList(); } }
    public bool IsBusy { get { lock (_sync) return _tasks.Any(t => t.Status is BackgroundTaskStatus.Running or BackgroundTaskStatus.Queued); } }

    public BackgroundTask Run(string name, Func<IProgress<ProgressInfo>, CancellationToken, Task> work)
    {
        var task = new BackgroundTask { Name = name };
        lock (_sync) _tasks.Add(task);
        var progress = new ThrottledProgress(p =>
        {
            task.Progress = p.Fraction; task.Message = p.Message;
            TaskChanged?.Invoke(this, task);
        });
        task.Status = BackgroundTaskStatus.Running;
        TaskChanged?.Invoke(this, task);
        _log.Info("Tasks", $"Started: {name}");
        task.Task = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await work(progress, task.Token).ConfigureAwait(false);
                task.Status = BackgroundTaskStatus.Completed;
                task.Progress = 1;
                _log.Success("Tasks", $"Completed: {name} ({task.Elapsed.TotalSeconds:0.0} s)");
            }
            catch (OperationCanceledException)
            {
                task.Status = BackgroundTaskStatus.Cancelled;
                _log.Warn("Tasks", $"Cancelled: {name}");
            }
            catch (Exception ex)
            {
                task.Status = BackgroundTaskStatus.Failed;
                task.Error = ex;
                _log.Error("Tasks", $"Failed: {name}: {ex.Message}");
            }
            finally
            {
                task.Finished = DateTime.Now;
                TaskChanged?.Invoke(this, task);
            }
        });
        return task;
    }

    public void ClearFinished()
    {
        lock (_sync) _tasks.RemoveAll(t => t.Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Cancelled);
    }

    /// <summary>Limits progress notifications to ~10 per second.</summary>
    private sealed class ThrottledProgress(Action<ProgressInfo> handler) : IProgress<ProgressInfo>
    {
        private long _last;
        public void Report(ProgressInfo value)
        {
            long now = Environment.TickCount64;
            if (value.Fraction < 1 && now - Interlocked.Read(ref _last) < 100) return;
            Interlocked.Exchange(ref _last, now);
            handler(value);
        }
    }
}
