namespace EventManagementService.Api;

public sealed record EventItem(int Id, string Title, DateOnly Date, string Location, string Description);
public sealed record EventRequest(string? Title, DateOnly? Date, string? Location, string? Description);

public static class EventValidation
{
    public static Dictionary<string, string[]> Validate(EventRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            errors["title"] = ["Название обязательно, не более 200 символов."];
        if (request.Date is null || request.Date == DateOnly.MinValue)
            errors["date"] = ["Укажите дату в формате YYYY-MM-DD."];
        if (string.IsNullOrWhiteSpace(request.Location) || request.Location.Length > 300)
            errors["location"] = ["Место обязательно, не более 300 символов."];
        if (request.Description?.Length > 2000)
            errors["description"] = ["Описание не более 2000 символов."];
        return errors;
    }
}
