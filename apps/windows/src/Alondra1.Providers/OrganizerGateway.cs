using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Alondra1.Core.Config;
using Alondra1.Core.Organizer;
using Alondra1.Core.Ports;
using Alondra1.Core.Security;

namespace Alondra1.Providers;

public sealed class OrganizerGateway : IOrganizerGateway
{
    private readonly HttpClient _http = new();
    private readonly OAuthClientSettings _clients;

    public OrganizerGateway(OAuthClientSettings clients) => _clients = clients;

    public Task<CapabilityStatus> GetStatusAsync(AccountConfig account, SecretBundle secrets, OrganizerFeature feature, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.Manual)
            return Task.FromResult(new CapabilityStatus(false, account.Id, account.DisplayName, OrganizerCopy.ManualUnavailable));
        if (!FeatureEnabled(feature))
            return Task.FromResult(new CapabilityStatus(false, account.Id, account.DisplayName, "Turn this feature on in Settings. Alondra1 asks for that permission only after you enable it."));
        var token = Token(account, secrets, feature);
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(new CapabilityStatus(false, account.Id, account.DisplayName, "Sign in again and grant access for this feature."));
        return Task.FromResult(new CapabilityStatus(true, account.Id, account.DisplayName, feature == OrganizerFeature.Notes ? OrganizerCopy.NotesAreAppNotes : null));
    }

    public async Task<IReadOnlyList<CalendarEntry>> ListEventsAsync(AccountConfig account, SecretBundle secrets, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.Microsoft365)
        {
            var url = $"https://graph.microsoft.com/v1.0/me/calendarView?startDateTime={Uri.EscapeDataString(start.UtcDateTime.ToString("o"))}&endDateTime={Uri.EscapeDataString(end.UtcDateTime.ToString("o"))}&$select=id,subject,start,end";
            using var document = await SendAsync(HttpMethod.Get, url, Token(account, secrets, OrganizerFeature.Calendar), null, graph: true, cancellationToken).ConfigureAwait(false);
            return ReadList(document, "value").Select(item => new CalendarEntry
            {
                Id = Str(item, "id"),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Subject = Str(item, "subject"),
                Start = GraphTime(item, "start"),
                End = GraphTime(item, "end"),
            }).ToList();
        }

        var google = $"https://www.googleapis.com/calendar/v3/calendars/primary/events?singleEvents=true&timeMin={Uri.EscapeDataString(start.UtcDateTime.ToString("o"))}&timeMax={Uri.EscapeDataString(end.UtcDateTime.ToString("o"))}";
        using var googleDoc = await SendAsync(HttpMethod.Get, google, Token(account, secrets, OrganizerFeature.Calendar), null, graph: false, cancellationToken).ConfigureAwait(false);
        return ReadList(googleDoc, "items").Select(item => new CalendarEntry
        {
            Id = Str(item, "id"),
            AccountId = account.Id,
            AccountName = account.DisplayName,
            Subject = Str(item, "summary"),
            Start = GoogleTime(item, "start"),
            End = GoogleTime(item, "end"),
            Revision = Str(item, "etag"),
        }).ToList();
    }

    public async Task<CalendarEntry> SaveEventAsync(AccountConfig account, SecretBundle secrets, CalendarEntry entry, CancellationToken cancellationToken)
    {
        entry.AccountId = account.Id;
        entry.AccountName = account.DisplayName;
        if (account.Kind == AccountKind.Microsoft365)
        {
            var payload = JsonSerializer.Serialize(new
            {
                subject = entry.Subject,
                start = new { dateTime = entry.Start.UtcDateTime.ToString("o"), timeZone = "UTC" },
                end = new { dateTime = entry.End.UtcDateTime.ToString("o"), timeZone = "UTC" },
            });
            var url = string.IsNullOrEmpty(entry.Id) ? "https://graph.microsoft.com/v1.0/me/events" : $"https://graph.microsoft.com/v1.0/me/events/{entry.Id}";
            var method = string.IsNullOrEmpty(entry.Id) ? HttpMethod.Post : HttpMethod.Patch;
            using var document = await SendAsync(method, url, Token(account, secrets, OrganizerFeature.Calendar), payload, true, cancellationToken).ConfigureAwait(false);
            entry.Id = Str(document.RootElement, "id");
            return entry;
        }

        var body = JsonSerializer.Serialize(new
        {
            summary = entry.Subject,
            start = new { dateTime = entry.Start.UtcDateTime.ToString("o") },
            end = new { dateTime = entry.End.UtcDateTime.ToString("o") },
        });
        var googleUrl = string.IsNullOrEmpty(entry.Id)
            ? "https://www.googleapis.com/calendar/v3/calendars/primary/events"
            : $"https://www.googleapis.com/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(entry.Id)}";
        using var saved = await SendAsync(string.IsNullOrEmpty(entry.Id) ? HttpMethod.Post : HttpMethod.Put, googleUrl, Token(account, secrets, OrganizerFeature.Calendar), body, false, cancellationToken).ConfigureAwait(false);
        entry.Id = Str(saved.RootElement, "id");
        entry.Revision = Str(saved.RootElement, "etag");
        return entry;
    }

    public Task DeleteEventAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken) =>
        account.Kind == AccountKind.Microsoft365
            ? SendNoContentAsync(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/events/{id}", Token(account, secrets, OrganizerFeature.Calendar), true, cancellationToken)
            : SendNoContentAsync(HttpMethod.Delete, $"https://www.googleapis.com/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(id)}", Token(account, secrets, OrganizerFeature.Calendar), false, cancellationToken);

    public async Task<IReadOnlyList<TaskEntry>> ListTasksAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var list = new List<TaskEntry>();
        if (account.Kind == AccountKind.Microsoft365)
        {
            using var lists = await SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/todo/lists", Token(account, secrets, OrganizerFeature.Tasks), null, true, cancellationToken).ConfigureAwait(false);
            foreach (var todoList in ReadList(lists, "value"))
            {
                var listId = Str(todoList, "id");
                using var tasks = await SendAsync(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks", Token(account, secrets, OrganizerFeature.Tasks), null, true, cancellationToken).ConfigureAwait(false);
                list.AddRange(ReadList(tasks, "value").Select(item => new TaskEntry
                {
                    Id = Str(item, "id"),
                    ListId = listId,
                    ListName = Str(todoList, "displayName"),
                    AccountId = account.Id,
                    AccountName = account.DisplayName,
                    Title = Str(item, "title"),
                    Completed = Str(item, "status").Equals("completed", StringComparison.OrdinalIgnoreCase),
                }));
            }

            return list;
        }

        using var googleLists = await SendAsync(HttpMethod.Get, "https://tasks.googleapis.com/tasks/v1/users/@me/lists", Token(account, secrets, OrganizerFeature.Tasks), null, false, cancellationToken).ConfigureAwait(false);
        foreach (var todoList in ReadList(googleLists, "items"))
        {
            var listId = Str(todoList, "id");
            using var tasks = await SendAsync(HttpMethod.Get, $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(listId)}/tasks", Token(account, secrets, OrganizerFeature.Tasks), null, false, cancellationToken).ConfigureAwait(false);
            list.AddRange(ReadList(tasks, "items").Select(item => new TaskEntry
            {
                Id = Str(item, "id"),
                ListId = listId,
                ListName = Str(todoList, "title"),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Title = Str(item, "title"),
                Completed = Str(item, "status").Equals("completed", StringComparison.OrdinalIgnoreCase),
                Revision = Str(item, "etag"),
            }));
        }

        return list;
    }

    public async Task<TaskEntry> SaveTaskAsync(AccountConfig account, SecretBundle secrets, TaskEntry entry, CancellationToken cancellationToken)
    {
        entry.AccountId = account.Id;
        entry.AccountName = account.DisplayName;
        if (account.Kind == AccountKind.Microsoft365)
        {
            var listId = string.IsNullOrEmpty(entry.ListId) ? await DefaultMicrosoftListAsync(account, secrets, cancellationToken).ConfigureAwait(false) : entry.ListId;
            var payload = JsonSerializer.Serialize(new { title = entry.Title, status = entry.Completed ? "completed" : "notStarted" });
            var url = string.IsNullOrEmpty(entry.Id)
                ? $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks"
                : $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks/{entry.Id}";
            using var document = await SendAsync(string.IsNullOrEmpty(entry.Id) ? HttpMethod.Post : HttpMethod.Patch, url, Token(account, secrets, OrganizerFeature.Tasks), payload, true, cancellationToken).ConfigureAwait(false);
            entry.Id = Str(document.RootElement, "id");
            entry.ListId = listId;
            return entry;
        }

        var googleList = string.IsNullOrEmpty(entry.ListId) ? "@default" : entry.ListId;
        var body = JsonSerializer.Serialize(new { title = entry.Title, status = entry.Completed ? "completed" : "needsAction" });
        var googleUrl = string.IsNullOrEmpty(entry.Id)
            ? $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(googleList)}/tasks"
            : $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(googleList)}/tasks/{Uri.EscapeDataString(entry.Id)}";
        using var saved = await SendAsync(string.IsNullOrEmpty(entry.Id) ? HttpMethod.Post : HttpMethod.Put, googleUrl, Token(account, secrets, OrganizerFeature.Tasks), body, false, cancellationToken).ConfigureAwait(false);
        entry.Id = Str(saved.RootElement, "id");
        entry.ListId = googleList;
        entry.Revision = Str(saved.RootElement, "etag");
        return entry;
    }

    public Task DeleteTaskAsync(AccountConfig account, SecretBundle secrets, string listId, string id, CancellationToken cancellationToken) =>
        account.Kind == AccountKind.Microsoft365
            ? SendNoContentAsync(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks/{id}", Token(account, secrets, OrganizerFeature.Tasks), true, cancellationToken)
            : SendNoContentAsync(HttpMethod.Delete, $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(id)}", Token(account, secrets, OrganizerFeature.Tasks), false, cancellationToken);

    public async Task<IReadOnlyList<ContactEntry>> ListContactsAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.Microsoft365)
        {
            using var document = await SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/contacts?$select=id,displayName,emailAddresses,mobilePhone", Token(account, secrets, OrganizerFeature.Contacts), null, true, cancellationToken).ConfigureAwait(false);
            return ReadList(document, "value").Select(item => new ContactEntry
            {
                Id = Str(item, "id"),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Name = Str(item, "displayName"),
                Email = FirstAddress(item, "emailAddresses", "address"),
                Phone = Str(item, "mobilePhone"),
            }).ToList();
        }

        using var google = await SendAsync(HttpMethod.Get, "https://people.googleapis.com/v1/people/me/connections?personFields=names,emailAddresses,phoneNumbers&pageSize=200", Token(account, secrets, OrganizerFeature.Contacts), null, false, cancellationToken).ConfigureAwait(false);
        return ReadList(google, "connections").Select(item => new ContactEntry
        {
            Id = Str(item, "resourceName"),
            AccountId = account.Id,
            AccountName = account.DisplayName,
            Name = FirstAddress(item, "names", "displayName"),
            Email = FirstAddress(item, "emailAddresses", "value"),
            Phone = FirstAddress(item, "phoneNumbers", "value"),
        }).ToList();
    }

    public async Task<ContactEntry> SaveContactAsync(AccountConfig account, SecretBundle secrets, ContactEntry entry, CancellationToken cancellationToken)
    {
        entry.AccountId = account.Id;
        entry.AccountName = account.DisplayName;
        if (account.Kind == AccountKind.Microsoft365)
        {
            var payload = JsonSerializer.Serialize(new
            {
                displayName = entry.Name,
                emailAddresses = new[] { new { address = entry.Email } },
                mobilePhone = entry.Phone,
            });
            var url = string.IsNullOrEmpty(entry.Id) ? "https://graph.microsoft.com/v1.0/me/contacts" : $"https://graph.microsoft.com/v1.0/me/contacts/{entry.Id}";
            using var document = await SendAsync(string.IsNullOrEmpty(entry.Id) ? HttpMethod.Post : HttpMethod.Patch, url, Token(account, secrets, OrganizerFeature.Contacts), payload, true, cancellationToken).ConfigureAwait(false);
            entry.Id = Str(document.RootElement, "id");
            return entry;
        }

        var body = JsonSerializer.Serialize(new
        {
            names = new[] { new { displayName = entry.Name } },
            emailAddresses = new[] { new { value = entry.Email } },
            phoneNumbers = string.IsNullOrEmpty(entry.Phone) ? null : new[] { new { value = entry.Phone } },
        });
        var googleUrl = string.IsNullOrEmpty(entry.Id)
            ? "https://people.googleapis.com/v1/people:createContact"
            : $"https://people.googleapis.com/v1/{entry.Id}:updateContact?updatePersonFields=names,emailAddresses,phoneNumbers";
        using var saved = await SendAsync(HttpMethod.Post, googleUrl, Token(account, secrets, OrganizerFeature.Contacts), body, false, cancellationToken).ConfigureAwait(false);
        entry.Id = Str(saved.RootElement, "resourceName");
        return entry;
    }

    public Task DeleteContactAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken) =>
        account.Kind == AccountKind.Microsoft365
            ? SendNoContentAsync(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/contacts/{id}", Token(account, secrets, OrganizerFeature.Contacts), true, cancellationToken)
            : SendNoContentAsync(HttpMethod.Delete, $"https://people.googleapis.com/v1/{id}:deleteContact", Token(account, secrets, OrganizerFeature.Contacts), false, cancellationToken);

    public async Task<IReadOnlyList<NoteEntry>> ListNotesAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        var files = account.Kind == AccountKind.Microsoft365
            ? await GraphNotesAsync(account, secrets, cancellationToken).ConfigureAwait(false)
            : await GoogleNotesAsync(account, secrets, cancellationToken).ConfigureAwait(false);
        return files;
    }

    public async Task<NoteEntry> SaveNoteAsync(AccountConfig account, SecretBundle secrets, NoteEntry entry, CancellationToken cancellationToken)
    {
        entry.AccountId = account.Id;
        entry.AccountName = account.DisplayName;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
        entry.Revision = Guid.NewGuid().ToString("n");
        var json = JsonSerializer.Serialize(new { entry.Id, entry.Title, entry.Body, entry.Revision, entry.UpdatedUtc });
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString("n");
        if (account.Kind == AccountKind.Microsoft365)
        {
            var url = $"https://graph.microsoft.com/v1.0/me/drive/special/approot:/{entry.Id}.json:/content";
            await SendRawAsync(HttpMethod.Put, url, Token(account, secrets, OrganizerFeature.Notes), Encoding.UTF8.GetBytes(json), "application/json", true, cancellationToken).ConfigureAwait(false);
            return entry;
        }

        if (string.IsNullOrEmpty(entry.Revision))
            entry.Revision = "1";
        var metadata = JsonSerializer.Serialize(new { name = entry.Id + ".json", parents = new[] { "appDataFolder" } });
        using var created = await SendAsync(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder", Token(account, secrets, OrganizerFeature.Notes), metadata, false, cancellationToken).ConfigureAwait(false);
        var fileId = Str(created.RootElement, "id");
        await SendRawAsync(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{fileId}?uploadType=media", Token(account, secrets, OrganizerFeature.Notes), Encoding.UTF8.GetBytes(json), "application/json", false, cancellationToken).ConfigureAwait(false);
        entry.Revision = fileId;
        return entry;
    }

    public Task DeleteNoteAsync(AccountConfig account, SecretBundle secrets, string id, CancellationToken cancellationToken) =>
        account.Kind == AccountKind.Microsoft365
            ? SendNoContentAsync(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/me/drive/special/approot:/{id}.json:", Token(account, secrets, OrganizerFeature.Notes), true, cancellationToken)
            : SendNoContentAsync(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}", Token(account, secrets, OrganizerFeature.Notes), false, cancellationToken);

    private async Task<IReadOnlyList<NoteEntry>> GraphNotesAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive/special/approot/children", Token(account, secrets, OrganizerFeature.Notes), null, true, cancellationToken).ConfigureAwait(false);
        var notes = new List<NoteEntry>();
        foreach (var item in ReadList(document, "value"))
        {
            var name = Str(item, "name");
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = Path.GetFileNameWithoutExtension(name);
            var download = Str(item, "@microsoft.graph.downloadUrl");
            if (string.IsNullOrEmpty(download))
                continue;
            var json = await _http.GetStringAsync(download, cancellationToken).ConfigureAwait(false);
            notes.Add(ParseNote(json, account, id));
        }

        return notes;
    }

    private async Task<IReadOnlyList<NoteEntry>> GoogleNotesAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder&fields=files(id,name)", Token(account, secrets, OrganizerFeature.Notes), null, false, cancellationToken).ConfigureAwait(false);
        var notes = new List<NoteEntry>();
        foreach (var item in ReadList(document, "files"))
        {
            var fileId = Str(item, "id");
            var json = await GetStringAsync($"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media", Token(account, secrets, OrganizerFeature.Notes), cancellationToken).ConfigureAwait(false);
            var note = ParseNote(json, account, Path.GetFileNameWithoutExtension(Str(item, "name")));
            note.Revision = fileId;
            notes.Add(note);
        }

        return notes;
    }

    private static NoteEntry ParseNote(string json, AccountConfig account, string fallbackId)
    {
        try
        {
            var note = JsonSerializer.Deserialize<NoteEntry>(json, ConfigJson.Options) ?? new NoteEntry();
            if (string.IsNullOrEmpty(note.Id))
                note.Id = fallbackId;
            note.AccountId = account.Id;
            note.AccountName = account.DisplayName;
            return note;
        }
        catch (JsonException)
        {
            return new NoteEntry { Id = fallbackId, AccountId = account.Id, AccountName = account.DisplayName, Title = fallbackId, Body = json };
        }
    }

    private async Task<string> DefaultMicrosoftListAsync(AccountConfig account, SecretBundle secrets, CancellationToken cancellationToken)
    {
        using var lists = await SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/todo/lists", Token(account, secrets, OrganizerFeature.Tasks), null, true, cancellationToken).ConfigureAwait(false);
        var first = ReadList(lists, "value").FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Undefined)
            return Str(first, "id");
        using var created = await SendAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/todo/lists", Token(account, secrets, OrganizerFeature.Tasks), "{\"displayName\":\"alondra1\"}", true, cancellationToken).ConfigureAwait(false);
        return Str(created.RootElement, "id");
    }

    private bool FeatureEnabled(OrganizerFeature feature) => feature switch
    {
        OrganizerFeature.Calendar => _clients.Calendar,
        OrganizerFeature.Tasks => _clients.Tasks,
        OrganizerFeature.Contacts => _clients.Contacts,
        OrganizerFeature.Notes => _clients.Notes,
        _ => false,
    };

    private static string? Token(AccountConfig account, SecretBundle secrets, OrganizerFeature feature)
    {
        _ = feature;
        return account.Kind == AccountKind.Gmail ? secrets.Google?.AccessToken : secrets.Graph?.AccessToken;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? token, string? json, bool graph, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? "");
        if (graph)
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response.StatusCode, body));
        if (string.IsNullOrWhiteSpace(body))
            return JsonDocument.Parse("{}");
        return JsonDocument.Parse(body);
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, string? token, bool graph, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(method, url, token, null, graph, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRawAsync(HttpMethod method, string url, string? token, byte[] content, string mediaType, bool graph, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? "");
        if (graph)
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(Explain(response.StatusCode, body));
        }
    }

    private async Task<string> GetStringAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? "");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Explain(response.StatusCode, body));
        return body;
    }

    private static string Explain(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Unauthorized || body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "The saved sign-in was revoked or expired. Connect the account again.";
        if ((int)status == 403 || body.Contains("ErrorAccessDenied", StringComparison.Ordinal) || body.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
            return OrganizerCopy.ConsentDenied;
        if (status == HttpStatusCode.NotFound || body.Contains("itemNotFound", StringComparison.OrdinalIgnoreCase) || body.Contains("notSupported", StringComparison.OrdinalIgnoreCase))
            return OrganizerCopy.LicenseOrPlan;
        if (body.Contains("syncStateNotFound", StringComparison.OrdinalIgnoreCase) || (int)status == 410)
            return "The saved change token is no longer valid. Alondra1 will read the folder again.";
        return string.IsNullOrWhiteSpace(body) ? $"The service returned {(int)status}." : body.Length > 280 ? body[..280] : body;
    }

    private static IEnumerable<JsonElement> ReadList(JsonDocument document, string property)
    {
        if (document.RootElement.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
            return array.EnumerateArray().ToList();
        return [];
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string FirstAddress(JsonElement element, string arrayName, string field)
    {
        if (!element.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return "";
        var first = array.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? Str(first, field) : "";
    }

    private static DateTimeOffset GraphTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return DateTimeOffset.UtcNow;
        var text = Str(value, "dateTime");
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset GoogleTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return DateTimeOffset.UtcNow;
        var text = Str(value, "dateTime");
        if (string.IsNullOrEmpty(text))
            text = Str(value, "date");
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }
}
