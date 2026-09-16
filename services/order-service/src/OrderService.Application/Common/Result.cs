namespace OrderService.Application.Common;

/// <summary>A use-case outcome: either a value or an <see cref="OrderError"/>.</summary>
public readonly struct Result<T>
{
    private Result(T value)
    {
        Value = value;
        Error = null;
    }

    private Result(OrderError error)
    {
        Value = default;
        Error = error;
    }

    public T? Value { get; }

    public OrderError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(OrderError error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(OrderError error) => Failure(error);
}
