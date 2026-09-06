using Microsoft.AspNetCore.Http;

namespace Lagerkraft.Shared;

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    private Result(T value)
    {
        IsSuccess = true;
        Value = value;
    }

    private Result(Error error)
    {
        Error = error;
    }

    public static Result<T> Ok(T value) => new(value);

    public static Result<T> Fail(string code, string message, object? details = null) =>
        new(new Error(code, message, details));

    public IResult ToHttp()
    {
        if (IsSuccess)
        {
            return Results.Ok(Value);
        }

        var error = Error!;
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: error.Code,
            detail: error.Message,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
                ["details"] = error.Details
            });
    }
}

public sealed record Error(string Code, string Message, object? Details = null);
