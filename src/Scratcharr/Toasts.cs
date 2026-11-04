namespace Scratcharr;

/// <summary>Short messages in the corner after an action ("Profile saved."). One per browser tab.</summary>
public sealed class Toasts
{
    public sealed record Toast(long Id, string Message, string Kind);

    private long _next;
    private readonly List<Toast> _items = [];

    public event Action? Changed;

    public IReadOnlyList<Toast> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }
    }

    public void Success(string message) => Show(message, "success");

    public void Warning(string message) => Show(message, "warning");

    public void Error(string message) => Show(message, "danger");

    public void Show(string message, string kind)
    {
        var toast = new Toast(Interlocked.Increment(ref _next), message, kind);
        lock (_items)
        {
            _items.Add(toast);
        }

        Changed?.Invoke();
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }

    public void Dismiss(long id)
    {
        lock (_items)
        {
            _items.RemoveAll(t => t.Id == id);
        }

        Changed?.Invoke();
    }
}
