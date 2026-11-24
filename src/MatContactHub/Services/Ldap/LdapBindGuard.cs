using System.Collections.Concurrent;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// Slows down guessing of LDAP passwords: the failed binds per client address within a window. An address with <see cref="LdapServerOptions.BindFailureLimit"/>
/// failures is refused (without the password being checked) until its oldest failure leaves the window. A successful bind does not clear the count (one known
/// account must not reset the guessing at another). Thread-safe; old addresses are forgotten as they go.
/// </summary>
public sealed class LdapBindGuard(int limit, TimeSpan window, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _failures = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _calls;

    /// <summary>Is the address refused right now?</summary>
    public bool IsBlocked(string address)
    {
        Sweep();
        if (!_failures.TryGetValue(address, out var queue))
        {
            return false;
        }

        lock (queue)
        {
            Drop(queue);
            return queue.Count >= limit;
        }
    }

    /// <summary>Counts a failed bind; answers how many the address has in the window now.</summary>
    public int Fail(string address)
    {
        var queue = _failures.GetOrAdd(address, _ => new Queue<DateTime>());
        lock (queue)
        {
            Drop(queue);
            queue.Enqueue(_time.GetUtcNow().UtcDateTime);
            return queue.Count;
        }
    }

    private void Drop(Queue<DateTime> queue)
    {
        var from = _time.GetUtcNow().UtcDateTime - window;
        while (queue.Count > 0 && queue.Peek() < from)
        {
            queue.Dequeue();
        }
    }

    /// <summary>Now and then: forget the addresses without a failure in the window.</summary>
    private void Sweep()
    {
        if (Interlocked.Increment(ref _calls) % 256 != 0)
        {
            return;
        }

        foreach (var (address, queue) in _failures)
        {
            lock (queue)
            {
                Drop(queue);
                if (queue.Count == 0)
                {
                    _failures.TryRemove(new KeyValuePair<string, Queue<DateTime>>(address, queue));
                }
            }
        }
    }
}
