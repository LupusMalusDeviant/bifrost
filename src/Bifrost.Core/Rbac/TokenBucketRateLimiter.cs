using System.Collections.Concurrent;
using Bifrost.Abstractions;

namespace Bifrost.Core.Rbac;

/// <summary>
/// Token-Bucket pro Identität (FR-31). Wirksames Limit = Maximum über die Rollen der Identität;
/// hat keine Rolle ein Limit, ist die Identität unbegrenzt. Kapazität = CallsPerMinute,
/// kontinuierliche Auffüllung. Unbekannte Identitäten werden abgelehnt (Default-Deny).
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private readonly IRbacDirectory _directory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<IdentityId, Bucket> _buckets = new();

    public TokenBucketRateLimiter(IRbacDirectory directory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool TryAcquire(IdentityId identity)
    {
        var resolved = _directory.GetIdentity(identity);
        if (resolved is null)
        {
            return false;
        }

        var limit = EffectiveLimit(resolved);
        if (limit is null)
        {
            return true;
        }

        var bucket = _buckets.GetOrAdd(identity, _ => new Bucket(limit.Value, _time.GetUtcNow()));
        return bucket.TryTake(limit.Value, _time.GetUtcNow());
    }

    private int? EffectiveLimit(Identity identity)
    {
        int? max = null;
        foreach (var roleId in identity.Roles)
        {
            var role = _directory.GetRole(roleId);
            if (role?.RateLimit is null)
            {
                continue;
            }

            max = max is null ? role.RateLimit.CallsPerMinute : Math.Max(max.Value, role.RateLimit.CallsPerMinute);
        }

        return max;
    }

    private sealed class Bucket
    {
        private readonly Lock _gate = new();
        private double _tokens;
        private DateTimeOffset _lastRefill;

        public Bucket(int capacity, DateTimeOffset now)
        {
            _tokens = capacity;
            _lastRefill = now;
        }

        public bool TryTake(int capacity, DateTimeOffset now)
        {
            lock (_gate)
            {
                var elapsed = (now - _lastRefill).TotalSeconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(capacity, _tokens + elapsed * capacity / 60.0);
                    _lastRefill = now;
                }

                if (_tokens < 1.0)
                {
                    return false;
                }

                _tokens -= 1.0;
                return true;
            }
        }
    }
}
