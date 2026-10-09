using System.Text.Json;
using EventManagementService.Api;
using Xunit;

namespace EventManagementService.Tests;

public sealed class EventValidationTests
{
    private static EventRequest Valid() => new("Конференция", new DateOnly(2026, 10, 15), "Москва", "Доклады");

    [Fact]
    public void ValidEventHasNoErrors() => Assert.Empty(EventValidation.Validate(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void BlankTitleIsRejected(string? title) =>
        Assert.Contains("title", EventValidation.Validate(Valid() with { Title = title }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLocationIsRejected(string? location) =>
        Assert.Contains("location", EventValidation.Validate(Valid() with { Location = location }));

    [Fact]
    public void MissingDateIsRejected() =>
        Assert.Contains("date", EventValidation.Validate(Valid() with { Date = null }));

    [Fact]
    public void DefaultDateIsRejected() =>
        Assert.Contains("date", EventValidation.Validate(Valid() with { Date = DateOnly.MinValue }));

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void TitleLimitIsEnforced(int length, bool accepted) =>
        Assert.Equal(accepted, !EventValidation.Validate(Valid() with { Title = new string('я', length) }).ContainsKey("title"));

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void LocationLimitIsEnforced(int length, bool accepted) =>
        Assert.Equal(accepted, !EventValidation.Validate(Valid() with { Location = new string('я', length) }).ContainsKey("location"));

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void DescriptionLimitIsEnforced(int length, bool accepted) =>
        Assert.Equal(accepted, !EventValidation.Validate(Valid() with { Description = new string('я', length) }).ContainsKey("description"));

    [Fact]
    public void DescriptionIsOptional() =>
        Assert.Empty(EventValidation.Validate(Valid() with { Description = null }));

    [Fact]
    public void AllInvalidFieldsAreReportedTogether() =>
        Assert.Equal(4, EventValidation.Validate(new(null, null, null, new string('x', 2001))).Count);

    [Fact]
    public void JsonDateUsesCalendarDateWithoutTimeZone()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(Valid(), options);
        Assert.Contains("\"date\":\"2026-10-15\"", json);
        Assert.Equal(Valid(), JsonSerializer.Deserialize<EventRequest>(json, options));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("2026-02-30")]
    public void MalformedCalendarDateCannotBeDeserialized(string date)
    {
        var json = "{\"title\":\"X\",\"date\":\"" + date + "\",\"location\":\"X\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EventRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

