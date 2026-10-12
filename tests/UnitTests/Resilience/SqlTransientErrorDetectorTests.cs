using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Exceptions;
using UrlShortener.Resilience;

namespace UrlShortener.UnitTests.Resilience;

// RSL-AC-01. SqlException has no public constructor, so the transient error-number set can't be exercised
// here without reflection; it is documented in SqlTransientErrorDetector and covered by code review.
public class SqlTransientErrorDetectorTests
{
    // ---- transient ---------------------------------------------------------------------

    [Fact]
    public void IsTransient_TimeoutException_ReturnsTrue()
    {
        SqlTransientErrorDetector.IsTransient(new TimeoutException()).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_TimeoutNestedInDbUpdateException_ReturnsTrue()
    {
        var ex = new DbUpdateException("save failed", new TimeoutException());

        SqlTransientErrorDetector.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_SocketException_ReturnsTrue()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);

        SqlTransientErrorDetector.IsTransient(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_SocketExceptionNestedInDbUpdateException_ReturnsTrue()
    {
        var ex = new DbUpdateException("save failed", new SocketException((int)SocketError.ConnectionRefused));

        SqlTransientErrorDetector.IsTransient(ex).Should().BeTrue();
    }

    // ---- not transient -----------------------------------------------------------------

    [Fact]
    public void IsTransient_DbUpdateExceptionWithNonTimeoutInner_ReturnsFalse()
    {
        // Stands in for an FK violation: the inner error is not a timeout or a transient SqlException.
        var ex = new DbUpdateException("FK violation", new InvalidOperationException("constraint"));

        SqlTransientErrorDetector.IsTransient(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_NotFoundException_ReturnsFalse()
    {
        SqlTransientErrorDetector.IsTransient(new NotFoundException("100001")).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_LinkExpiredException_ReturnsFalse()
    {
        SqlTransientErrorDetector.IsTransient(new LinkExpiredException("100001")).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_ConcurrencyConflictException_ReturnsFalse()
    {
        var ex = new ConcurrencyConflictException(new DbUpdateConcurrencyException("row version changed"));

        SqlTransientErrorDetector.IsTransient(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_InvalidOperationException_ReturnsFalse()
    {
        SqlTransientErrorDetector.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_Null_ReturnsFalse()
    {
        SqlTransientErrorDetector.IsTransient(null).Should().BeFalse();
    }
}
