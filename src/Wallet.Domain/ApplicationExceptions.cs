namespace Wallet.Domain;

public sealed class ConflictException(string message) : DomainException(message);

public sealed class NotFoundException(string message) : DomainException(message);

public sealed class InvalidCredentialsException() : DomainException("Invalid email or password.");

public sealed class IdempotencyConflictException()
    : DomainException("This Idempotency-Key was already used with a different request payload.");

public sealed class AppValidationException(IDictionary<string, string[]> errors)
    : DomainException("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static AppValidationException For(string field, string message)
        => new(new Dictionary<string, string[]> { [field] = new[] { message } });
}
