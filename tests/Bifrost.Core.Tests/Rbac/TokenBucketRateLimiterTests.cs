using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Rbac;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bifrost.Core.Tests.Rbac;

public class TokenBucketRateLimiterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryRbacDirectory _directory = new();

    private IdentityId Register(params RateLimit?[] roleLimits)
    {
        var roleIds = new List<RoleId>();
        foreach (var limit in roleLimits)
        {
            var role = new Role(RoleId.New(), $"r{roleIds.Count}", [], limit);
            _directory.UpsertRole(role);
            roleIds.Add(role.Id);
        }

        var id = IdentityId.New();
        _directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, roleIds));
        return id;
    }

    [Fact]
    public void Limit_allows_burst_up_to_capacity_then_denies()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);
        var id = Register(new RateLimit(60));

        for (var i = 0; i < 60; i++)
        {
            limiter.TryAcquire(id).Should().BeTrue($"Call {i + 1} liegt innerhalb der Kapazität");
        }

        limiter.TryAcquire(id).Should().BeFalse("Kapazität 60 ist erschöpft");
    }

    [Fact]
    public void Tokens_refill_continuously_over_time()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);
        var id = Register(new RateLimit(60));
        for (var i = 0; i < 60; i++)
        {
            limiter.TryAcquire(id);
        }

        limiter.TryAcquire(id).Should().BeFalse();

        _time.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire(id).Should().BeTrue("60/min = 1 Token pro Sekunde");
        limiter.TryAcquire(id).Should().BeFalse("nur 1 Token wurde aufgefüllt");
    }

    [Fact]
    public void Identity_without_any_limit_is_unlimited()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);
        var id = Register(null, null);

        for (var i = 0; i < 1000; i++)
        {
            limiter.TryAcquire(id).Should().BeTrue();
        }
    }

    [Fact]
    public void Most_permissive_role_limit_wins()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);
        var id = Register(new RateLimit(2), new RateLimit(10));

        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire(id).Should().BeTrue($"Maximum über Rollen ist 10, Call {i + 1}");
        }

        limiter.TryAcquire(id).Should().BeFalse();
    }

    [Fact]
    public void Unknown_identity_is_denied()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);

        limiter.TryAcquire(IdentityId.New()).Should().BeFalse("Default-Deny gilt auch fürs Rate-Limit");
    }

    [Fact]
    public void Refill_never_exceeds_capacity()
    {
        var limiter = new TokenBucketRateLimiter(_directory, _time);
        var id = Register(new RateLimit(5));

        _time.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
        {
            limiter.TryAcquire(id).Should().BeTrue();
        }

        limiter.TryAcquire(id).Should().BeFalse("Bucket ist auf Kapazität 5 gedeckelt, egal wie lange Pause war");
    }
}
