using System;
using System.Linq;
using System.Text.Json;
using CS2MultiplayerMod.Game;

internal static class SessionFeedChecks
{
    public static void Run(Action<bool, string> check)
    {
        var feed = new SessionFeed();
        feed.AddMessage("Alice", "Quotes \" and a newline\nwith a slash \\ and tab\t.", false);
        feed.AddMessage("Alice", "Same name, local player.", true);
        feed.AddActivity("World sync started.");
        var entries = Read(feed);
        check(entries[0].GetProperty("kind").GetString() == "message" &&
              !entries[0].GetProperty("isLocal").GetBoolean(), "remote player chat is explicitly typed");
        check(entries[1].GetProperty("isLocal").GetBoolean(), "local chat is identified independently of player name");
        check(entries[0].GetProperty("timestamp").GetInt64() > 0 &&
              entries[1].GetProperty("timestamp").GetInt64() >= entries[0].GetProperty("timestamp").GetInt64(),
            "chat timestamps include the date and preserve append order for grouping");
        check(entries[0].GetProperty("text").GetString().Contains("\nwith a slash \\"), "feed JSON preserves escaped player text");
        check(entries[2].GetProperty("sender").ValueKind == JsonValueKind.Null &&
              entries[2].GetProperty("kind").GetString() == "activity", "mod notices are activity, not player messages");

        feed.AddMessage("Bob", "Chat between duplicate notices.", false);
        string cached = feed.Json;
        feed.AddActivity("World sync started.");
        check(ReferenceEquals(cached, feed.Json), "a repeated activity burst leaves the binding unchanged even across player chat");
        feed.AddActivity("World sync started.", error: true);
        check(Read(feed).Last().GetProperty("kind").GetString() == "error", "an error remains distinct from an informational notice");
        feed.AddMessage("Bob", "Chat between duplicate notices.", false);
        check(Read(feed).Count(entry => entry.GetProperty("text").GetString() == "Chat between duplicate notices.") == 2,
            "repeated player messages are retained");

        feed.Clear();
        for (int i = 0; i < SessionFeed.MessageLimit; i++) feed.AddMessage("Alice", "message " + i, false);
        for (int i = 0; i < SessionFeed.ActivityLimit + 40; i++) feed.AddActivity("activity " + i);
        entries = Read(feed);
        check(entries.Count(IsMessage) == SessionFeed.MessageLimit && entries.Any(entry => entry.GetProperty("text").GetString() == "message 0"),
            "a flood of mod activity cannot evict player conversation");
        check(entries.Count(entry => !IsMessage(entry)) == SessionFeed.ActivityLimit, "activity history has its own bounded budget");
        for (int i = 0; i < 40; i++) feed.AddMessage("Bob", "new message " + i, false);
        entries = Read(feed);
        check(entries.Count(IsMessage) == SessionFeed.MessageLimit && !entries.Any(entry => entry.GetProperty("text").GetString() == "message 0"),
            "player history evicts only its oldest messages");
        check(entries.Count(entry => !IsMessage(entry)) == SessionFeed.ActivityLimit, "player chat cannot evict activity history");

        int lastId = entries.Last().GetProperty("id").GetInt32();
        feed.Clear();
        check(feed.Json == "[]", "a new session has no stale history");
        feed.AddActivity("activity " + (SessionFeed.ActivityLimit + 39));
        entries = Read(feed);
        check(entries.Length == 1 && entries[0].GetProperty("id").GetInt32() > lastId,
            "clearing resets duplicate suppression and preserves increasing unread IDs");
        cached = feed.Json;
        feed.AddMessage("Bob", "   ", false);
        feed.AddActivity(null);
        check(ReferenceEquals(cached, feed.Json), "empty entries never rebuild the UI binding");
    }

    private static bool IsMessage(JsonElement entry) => entry.GetProperty("kind").GetString() == "message";

    private static JsonElement[] Read(SessionFeed feed)
    {
        using var json = JsonDocument.Parse(feed.Json);
        return json.RootElement.EnumerateArray().Select(entry => entry.Clone()).ToArray();
    }
}
