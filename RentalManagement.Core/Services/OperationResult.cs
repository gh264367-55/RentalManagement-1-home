// =====================================================================================================================
// Module: RentalManagement.Core / Services / OperationResult.cs
// Purpose: The return type of every service command. Expected failures (broken business rules, records the user may
//          not see, stale or disallowed changes) are reported as values with a FailureKind and per-field messages,
//          instead of exceptions, so controllers can map them to 404 / 409 / 422 and show messages next to fields.
// =====================================================================================================================

namespace RentalManagement.Core.Services;

/// <summary>Why a command failed, so the controller can choose the right response.</summary>
public enum FailureKind
{
    /// <summary>The input broke a rule. Show the messages next to the fields and let the user fix them.</summary>
    Validation,

    /// <summary>The record doesn't exist or isn't visible to this user. Respond 404 so ids aren't disclosed.</summary>
    NotFound,

    /// <summary>The record exists but its state doesn't allow this (e.g. editing a submitted application, a stale save).</summary>
    Conflict,
}

/// <summary>A message for one input field; a null field means the whole form.</summary>
/// <param name="Field">The input property name the message belongs to, or null for a form-level message.</param>
/// <param name="Message">The user-facing message.</param>
public sealed record FieldError(string? Field, string Message);

/// <summary>
/// Outcome of a service command. Expected failures (a broken business rule, a stale edit, a record the user may not
/// touch) are returned, not thrown, each with the input field it belongs to so the controller can show the message
/// next to that field.
/// </summary>
public class OperationResult
{
    /// <summary>Creates a result; use the static factory methods instead.</summary>
    /// <param name="succeeded">Whether the command succeeded.</param>
    /// <param name="kind">The failure kind (ignored on success).</param>
    /// <param name="errors">The messages (empty on success).</param>
    protected OperationResult(bool succeeded, FailureKind kind, IReadOnlyList<FieldError> errors)
    {
        Succeeded = succeeded;
        Kind = kind;
        Errors = errors;
    }

    /// <summary>True when the command did what was asked.</summary>
    public bool Succeeded { get; }

    /// <summary>Meaningful only when <see cref="Succeeded"/> is false.</summary>
    public FailureKind Kind { get; }

    /// <summary>Every problem found; empty on success.</summary>
    public IReadOnlyList<FieldError> Errors { get; }

    /// <summary>The first message, or null on success.</summary>
    public string? Error => Errors.Count > 0 ? Errors[0].Message : null;

    /// <summary>The field of the first message, or null.</summary>
    public string? Field => Errors.Count > 0 ? Errors[0].Field : null;

    /// <summary>A successful result.</summary>
    /// <returns>The result.</returns>
    public static OperationResult Success() => new(true, FailureKind.Validation, []);

    /// <summary>A validation failure with one message.</summary>
    /// <param name="error">The message.</param>
    /// <param name="field">The input field it belongs to, or null for a form-level message.</param>
    /// <returns>The result.</returns>
    public static OperationResult Failure(string error, string? field = null) =>
        new(false, FailureKind.Validation, [new FieldError(field, error)]);

    /// <summary>A validation failure with several messages (e.g. every invalid field of a section).</summary>
    /// <param name="errors">The messages.</param>
    /// <returns>The result.</returns>
    public static OperationResult Invalid(IReadOnlyList<FieldError> errors) =>
        new(false, FailureKind.Validation, errors);

    /// <summary>The record doesn't exist or isn't visible to the user.</summary>
    /// <param name="error">The message.</param>
    /// <returns>The result.</returns>
    public static OperationResult NotFound(string error) => new(false, FailureKind.NotFound, [new FieldError(null, error)]);

    /// <summary>The record's current state doesn't allow the command.</summary>
    /// <param name="error">The message explaining why.</param>
    /// <returns>The result.</returns>
    public static OperationResult Conflict(string error) => new(false, FailureKind.Conflict, [new FieldError(null, error)]);
}

/// <summary>An <see cref="OperationResult"/> that carries a value on success (for example a new record's id).</summary>
/// <typeparam name="T">The type of the value.</typeparam>
public sealed class OperationResult<T> : OperationResult
{
    /// <summary>Creates a result; use the static factory methods instead.</summary>
    private OperationResult(bool succeeded, T? value, FailureKind kind, IReadOnlyList<FieldError> errors)
        : base(succeeded, kind, errors) => Value = value;

    /// <summary>The value produced on success; default on failure.</summary>
    public T? Value { get; }

    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    /// <param name="value">The value produced.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> Success(T value) => new(true, value, FailureKind.Validation, []);

    /// <summary>A validation failure with one message.</summary>
    /// <param name="error">The message.</param>
    /// <param name="field">The input field it belongs to, or null for a form-level message.</param>
    /// <returns>The result.</returns>
    public static new OperationResult<T> Failure(string error, string? field = null) =>
        new(false, default, FailureKind.Validation, [new FieldError(field, error)]);

    /// <summary>Carries a failure from a non-generic result.</summary>
    /// <param name="failure">The failed result to copy the kind and messages from.</param>
    /// <returns>The result.</returns>
    public static OperationResult<T> From(OperationResult failure) => new(false, default, failure.Kind, failure.Errors);
}
