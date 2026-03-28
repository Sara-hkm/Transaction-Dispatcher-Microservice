using System.ComponentModel.DataAnnotations;
using TransactionDispatch.Application.Validation;

namespace TransactionDispatch.Tests;

public sealed class NotWhiteSpaceAttributeTests
{
    private static ValidationResult? Validate(object? value, string? customError = null)
    {
        var attr = new NotWhiteSpaceAttribute();
        if (customError is not null)
            attr.ErrorMessage = customError;

        var ctx = new ValidationContext(new object()) { DisplayName = "TestProp" };
        return attr.GetValidationResult(value, ctx);
    }

    [Fact]
    public void IsValid_NullValue_ReturnsSuccess()
        => Assert.Equal(ValidationResult.Success, Validate(null));

    [Fact]
    public void IsValid_EmptyString_ReturnsSuccess()
        => Assert.Equal(ValidationResult.Success, Validate(string.Empty));

    [Fact]
    public void IsValid_NormalString_ReturnsSuccess()
        => Assert.Equal(ValidationResult.Success, Validate("/some/valid/path"));

    [Fact]
    public void IsValid_StringWithContent_ReturnsSuccess()
        => Assert.Equal(ValidationResult.Success, Validate("  value  "));

    [Fact]
    public void IsValid_WhitespaceOnly_ReturnsValidationError()
    {
        var result = Validate("   ");
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public void IsValid_TabAndNewlineOnly_ReturnsValidationError()
    {
        var result = Validate("\t\n\r");
        Assert.NotNull(result);
    }

    [Fact]
    public void IsValid_WhitespaceWithCustomErrorMessage_UsesCustomMessage()
    {
        var result = Validate("   ", "My custom error");
        Assert.NotNull(result);
        Assert.Equal("My custom error", result.ErrorMessage);
    }

    [Fact]
    public void IsValid_WhitespaceWithNoCustomMessage_UsesDisplayNameInMessage()
    {
        var result = Validate("   ");
        Assert.NotNull(result);
        Assert.Contains("TestProp", result.ErrorMessage);
    }
}
