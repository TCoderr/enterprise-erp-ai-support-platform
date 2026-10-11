using Microsoft.Data.SqlClient;

namespace Kantar.Infrastructure.Persistence;

public enum SqlErrorClass
{
    None,
    UniqueViolation,
    Unavailable,
    Other,
}

/// <summary>Lets the HTTP layer classify a database failure without referencing provider types.</summary>
public static class SqlErrors
{
    public static SqlErrorClass Classify(Exception exception)
    {
        var sql = SqlUnitOfWork.FindSqlException(exception);
        if (sql is null)
        {
            return exception is TimeoutException || exception.InnerException is TimeoutException
                ? SqlErrorClass.Unavailable
                : SqlErrorClass.None;
        }

        return sql.Number switch
        {
            2601 or 2627 => SqlErrorClass.UniqueViolation,

            // Connection-level failures, login timeouts, server not reachable, database not available.
            -2 or -1 or 2 or 20 or 53 or 64 or 233 or 258 or 4060 or 10053 or 10054 or 10060 or 10061 or 18456 or 1205 or 1222 => SqlErrorClass.Unavailable,
            _ => IsConnectionBroken(sql) ? SqlErrorClass.Unavailable : SqlErrorClass.Other,
        };
    }

    private static bool IsConnectionBroken(SqlException exception) => exception.Class >= 20;
}
