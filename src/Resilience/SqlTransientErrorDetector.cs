using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace UrlShortener.Resilience;

/// <summary>
/// Decides whether a failure is worth retrying. Only errors that can succeed on a second attempt count:
/// timeouts, dropped connections, Azure SQL throttling/failover and deadlock victims. Constraint violations,
/// validation and domain exceptions (not found, expired, concurrency conflict) are never retried.
/// </summary>
public static class SqlTransientErrorDetector
{
    // Same family of numbers EF Core's SqlServerRetryingExecutionStrategy retries on:
    //   -2 timeout, 20/64/233/10053/10054/10060 connection loss, 1205 deadlock victim,
    //   2/53/10061/11001 server not found or connection refused (e.g. SQL Server stopped or still starting),
    //   4060/4221 database not available yet,
    //   10928/10929/40501/40540 resource throttling, 40143/40197/40613/49918-49920 Azure SQL failover.
    private static readonly HashSet<int> TransientNumbers = new()
    {
        -2, 2, 20, 53, 64, 233, 1205, 4060, 4221, 10053, 10054, 10060, 10061, 10928, 10929,
        11001, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920
    };

    /// <summary>
    /// True if the exception, or any inner exception, is a transient SQL error, a timeout or a socket error.
    /// SqlClient on Linux can report a refused connection as a provider error wrapping a SocketException.
    /// </summary>
    public static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or SocketException)
            {
                return true;
            }

            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)))
            {
                return true;
            }
        }

        return false;
    }
}
