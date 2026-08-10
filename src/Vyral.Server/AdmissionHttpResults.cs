using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vyral.Execution;
using Vyral.Primitives;

namespace Vyral.Server;

internal static class AdmissionHttpResults
{
    public static IResult From<T>(string location, T resource, AdmissionReceipt admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(admission);

        if (string.Equals(admission.Status, AdmissionStatuses.Accepted, StringComparison.Ordinal))
        {
            return Results.Accepted(location, resource);
        }

        var statusCode = GetRejectionStatusCode(admission.FailureClass);
        var problem = new ProblemDetails
        {
            Type = "https://openvyral.com/problems/admission-rejected",
            Title = "Admission rejected",
            Status = statusCode,
            Detail = admission.Error ?? "The execution runtime did not admit this request."
        };
        problem.Extensions["admission"] = admission;
        return Results.Problem(problem);
    }

    private static int GetRejectionStatusCode(string? failureClass) => failureClass switch
    {
        ExecutionFailureClasses.QueueFull => StatusCodes.Status429TooManyRequests,
        ExecutionFailureClasses.Platform => StatusCodes.Status503ServiceUnavailable,
        ExecutionFailureClasses.HandlerMissing or ExecutionFailureClasses.PluginMismatch => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status422UnprocessableEntity
    };
}
