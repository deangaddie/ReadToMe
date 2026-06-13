namespace Read2Me.Core.Models
{
    public readonly record struct Result
    {
        private readonly string? _error;

        public bool IsSuccess => _error is null;
        public string Error => _error ?? string.Empty;

        private Result(string? error) => _error = error;

        public static Result Ok() => new(null);
        public static Result Fail(string error) => new(error);
    }

    public readonly record struct Result<T>
    {
        private readonly T? _value;
        private readonly string? _error;

        public bool IsSuccess => _error is null;
        public T Value => _value!;
        public string Error => _error ?? string.Empty;

        private Result(T? value, string? error) { _value = value; _error = error; }

        public static Result<T> Ok(T value) => new(value, null);
        public static Result<T> Fail(string error) => new(default, error);
    }
}
