using System.Collections.Concurrent;

namespace MatContactHub.Services.Ldap;

/// <summary>
/// Slows down guessing of LDAP passwords: the failed binds per client address within a window. An address with <see cref="LdapServerOptions.BindFailureLimit"/>
/// failures is refused (without the password being checked) until its oldest failure leaves the window. A bind reserves its attempt before the password is checked
/// (<see cref="TryBegin"/>), so binds that arrive at the same time on several connections count too: an address never has more attempts in the window than the limit.
/// A successful bind does not clear the count (one known account must not reset the guessing at another). Thread-safe; old addresses are forgotten as they go.
/// </summary>
public sealed class LdapBindGuard(int limit, TimeSpan window, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, State> _addresses = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _calls;

    /// <summary>Is the address refused right now (its failures and the attempts still being checked reach the limit)?</summary>
    public bool IsBlocked(string address)
    {
        if (!_addresses.TryGetValue(address, out var state))
        {
            return false;
        }

        lock (state)
        {
            Drop(state.Failures);
            return state.Failures.Count + state.Pending >= limit;
        }
    }

    /// <summary>
    /// Reserves a bind attempt of the address before its password is checked; false (nothing reserved) when the address is refused. Every true is followed by
    /// <see cref="Succeeded"/> or <see cref="Failed"/>.
    /// </summary>
    public bool TryBegin(string address)
    {
        Sweep();
        while (true)
        {
            var state = _addresses.GetOrAdd(address, _ => new State());
            lock (state)
            {
                if (state.Removed)
                {
                    // swept away just now: take the new one
                    continue;
                }

                Drop(state.Failures);
                if (state.Failures.Count + state.Pending >= limit)
                {
                    return false;
                }

                state.Pending++;
                return true;
            }
        }
    }

    /// <summary>The reserved attempt succeeded: it does not count.</summary>
    public void Succeeded(string address) => End(address, failed: false);

    /// <summary>The reserved attempt failed: it counts; answers how many failures the address has in the window now.</summary>
    public int Failed(string address) => End(address, failed: true);

    private int End(string address, bool failed)
    {
        // a pending attempt keeps its state from being swept, so it is still there
        if (!_addresses.TryGetValue(address, out var state))
        {
            return 0;
        }

        lock (state)
        {
            state.Pending = Math.Max(0, state.Pending - 1);
            Drop(state.Failures);
            if (failed)
            {
                state.Failures.Enqueue(_time.GetUtcNow().UtcDateTime);
            }

            return state.Failures.Count;
        }
    }

    private void Drop(Queue<DateTime> failures)
    {
        var from = _time.GetUtcNow().UtcDateTime - window;
        while (failures.Count > 0 && failures.Peek() < from)
        {
            failures.Dequeue();
        }
    }

    /// <summary>Now and then: forget the addresses without a failure in the window and without an attempt being checked.</summary>
    private void Sweep()
    {
        if (Interlocked.Increment(ref _calls) % 256 != 0)
        {
            return;
        }

        foreach (var (address, state) in _addresses)
        {
            lock (state)
            {
                Drop(state.Failures);
                if (state.Failures.Count == 0 && state.Pending == 0)
                {
                    state.Removed = true;
                    _addresses.TryRemove(new KeyValuePair<string, State>(address, state));
                }
            }
        }
    }

    private sealed class State
    {
        public Queue<DateTime> Failures { get; } = new();

        public int Pending { get; set; }

        public bool Removed { get; set; }
    }
}
