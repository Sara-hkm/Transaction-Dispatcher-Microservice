using System.ComponentModel.DataAnnotations;

namespace TransactionDispatch.Application.Validation;

/// <summary>
/// Validates that a string property is not whitespace-only.
/// Null and empty strings are handled by <see cref="RequiredAttribute"/>; this attribute
/// adds the complementary check for strings that contain only whitespace characters.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotWhiteSpaceAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string { Length: > 0 } str && string.IsNullOrWhiteSpace(str))
            return new ValidationResult(ErrorMessage ?? $"{validationContext.DisplayName} must not be empty or whitespace.");

        return ValidationResult.Success;
    }
}
