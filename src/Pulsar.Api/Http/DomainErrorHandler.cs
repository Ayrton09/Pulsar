using Microsoft.AspNetCore.Diagnostics;
using Pulsar.Application.Abstractions;

namespace Pulsar.Api.Http;

/// <summary>
/// Answers a <see cref="DomainErrorException"/> as its error's problem (a stored template this panel refuses is a 409,
/// not a 500), and logs what the exception says besides.
/// </summary>
public sealed partial class DomainErrorHandler(ILogger<DomainErrorHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not DomainErrorException domain)
        {
            return false;
        }

        LogRefused(domain.Error.Code, domain.Message);
        await ApiProblems.FromDomainError(domain.Error).ExecuteAsync(httpContext);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request refused with {Code}: {Detail}")]
    private partial void LogRefused(string code, string detail);
}
