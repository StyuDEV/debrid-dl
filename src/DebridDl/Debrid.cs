using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DebridDl;

public sealed class DebridException(string code, string message) : Exception(message)
{
    public string Code => code;
}

public static class Debrid
{
    static readonly HttpClient http = new()
    {
        BaseAddress = new("https://api.alldebrid.com/v4/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static string ApiKey { get; set; } = "";

    public static async Task<string?> Unlock(string link, CancellationToken ct)
    {
        if (ApiKey == "")
        {
            return null;
        }

        JsonElement data;
        try
        {
            data = await Get($"link/unlock?link={Uri.EscapeDataString(link)}", ct);
        }
        catch (DebridException e) when (e.Code == "LINK_HOST_NOT_SUPPORTED")
        {
            return null;
        }

        if (data.TryGetProperty("link", out var direct) && direct.GetString() is { Length: > 0 } url)
        {
            return url;
        }
        if (!data.TryGetProperty("delayed", out var delayed))
        {
            throw new DebridException("NO_LINK", "AllDebrid n'a pas donné de lien");
        }

        while (true)
        {
            await Task.Delay(5000, ct);
            var state = await Get($"link/delayed?id={delayed}", ct);
            switch (state.GetProperty("status").GetInt32())
            {
                case 2:
                    return state.GetProperty("link").GetString();
                case 3:
                    throw new DebridException("DELAYED", "AllDebrid n'a pas pu générer le lien");
            }
        }
    }

    public static async Task<string> Account()
    {
        var user = (await Get("user")).GetProperty("user");
        var name = user.GetProperty("username").GetString();
        if (!user.GetProperty("isPremium").GetBoolean())
        {
            return $"{name} · pas premium";
        }

        var until = DateTimeOffset.FromUnixTimeSeconds(user.GetProperty("premiumUntil").GetInt64());
        return $"{name} · premium jusqu'au {until.LocalDateTime:d MMMM yyyy}";
    }

    public static async Task<HashSet<string>> Hosts()
    {
        var data = await Get("hosts/domains");
        string[] lists = ["hosts", "redirectors"];
        return lists
            .SelectMany(k => data.GetProperty(k).EnumerateArray())
            .Select(h => h.GetString()!)
            .ToHashSet();
    }

    static async Task<JsonElement> Get(string path, CancellationToken ct = default)
    {
        var uri = path + (path.Contains('?') ? '&' : '?') + "agent=debrid-dl";
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        using var res = await http.SendAsync(req, ct);
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() == "success")
        {
            return root.GetProperty("data").Clone();
        }

        var error = root.GetProperty("error");
        var code = error.GetProperty("code").GetString() ?? "";
        throw new DebridException(code, error.GetProperty("message").GetString() ?? code);
    }
}
