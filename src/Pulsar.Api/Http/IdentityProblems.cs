using Microsoft.AspNetCore.Identity;

namespace Pulsar.Api.Http;

/// <summary>Turns ASP.NET Core Identity errors (password policy, duplicate names…) into a validation problem.</summary>
public static class IdentityProblems
{
    public static IResult Validation(IdentityResult result)
    {
        var errors = result.Errors
            .GroupBy(e => FieldFor(e.Code))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return TypedResults.ValidationProblem(errors,
            extensions: new Dictionary<string, object?>
            {
                [ApiProblems.CodeExtension] = "validation.failed",
                ["identityErrors"] = result.Errors.Select(e => e.Code).ToArray(),
            });
    }

    private static string FieldFor(string code) => code switch
    {
        _ when code.StartsWith("Password", StringComparison.Ordinal) => "password",
        _ when code.Contains("UserName", StringComparison.Ordinal) => "userName",
        _ when code.Contains("Email", StringComparison.Ordinal) => "email",
        _ => string.Empty,
    };
}
