using System.Collections.Generic;
using System.Linq;

namespace CodeFusion.Data.Tests;

/// <summary>
/// <see cref="ConduitFixture.Seed"/> as data, and the JSON the RealWorld spec gives for it: the oracle the
/// execution tests compare replies with. Written from the spec and the seed, not from the generated code.
/// </summary>
static class SeedModel
{
    public sealed record U(long Id, string Username, string? Bio, string? Image);
    public sealed record A(long Id, string Slug, string Title, string Description, string Body, string Created, string Updated, long Author);

    public static readonly U[] Users =
    [
        new(1, "alice", null, null),
        new(2, "bob", "I write \"quotes\" and naïve UTF-8 ✓", null),
        new(3, "carol", null, "https://img/c.png"),
        new(4, "dave", "dave", null),
    ];

    public static readonly A[] Articles =
    [
        new(1, "how-to-train-your-dragon", "How to train your dragon", "Ever wonder how?", "You have to believe", "2025-04-01T12:00:00.000Z", "2025-04-01T12:00:00.000Z", 2),
        new(2, "dragons-2", "Dragons 2", "More", "Body 2", "2025-04-01T12:01:00.000Z", "2025-04-01T12:01:30.123Z", 2),
        new(3, "carols-one", "Carol one", "C1", "Body 3", "2025-04-01T12:02:00.000Z", "2025-04-01T12:02:00.000Z", 3),
        new(4, "carols-two", "Carol two", "C2", "Body 4", "2025-04-01T12:03:00.000Z", "2025-04-01T12:03:00.000Z", 3),
        new(5, "alices", "Alice's", "A", "Body 5", "2025-04-01T12:04:00.000Z", "2025-04-01T12:04:00.000Z", 1),
    ];

    public static readonly (long Follower, long Followee)[] Follows = [(1, 2), (1, 3)];
    public static readonly (long User, long Article)[] Favorites = [(1, 1), (1, 3), (4, 1)];
    public static readonly Dictionary<long, string[]> Tags = new() { [1] = ["dragons", "training"], [2] = ["dragons"], [5] = ["zebra"] };

    /// <summary>A JSON string as Utf8JsonWriter writes it with relaxed escaping: quotes and backslashes escaped, UTF-8 as is.</summary>
    public static string Str(string? s) => s is null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static bool Following(long viewer, long user) => viewer != 0 && Follows.Contains((viewer, user));

    public static string Profile(long user, long viewer, bool? following = null)
    {
        var u = Users.Single(x => x.Id == user);
        return $"{{\"username\":{Str(u.Username)},\"bio\":{Str(u.Bio)},\"image\":{Str(u.Image)},\"following\":{Bool(following ?? Following(viewer, user))}}}";
    }

    static string Bool(bool b) => b ? "true" : "false";

    public static string Article(long id, long viewer, bool body)
    {
        var a = Articles.Single(x => x.Id == id);
        string tags = "[" + string.Join(",", (Tags.TryGetValue(id, out var t) ? t : []).Select(Str)) + "]";
        int count = Favorites.Count(f => f.Article == id);
        bool favorited = viewer != 0 && Favorites.Contains((viewer, id));
        return $"{{\"slug\":{Str(a.Slug)},\"title\":{Str(a.Title)},\"description\":{Str(a.Description)},{(body ? $"\"body\":{Str(a.Body)}," : "")}" +
            $"\"createdAt\":\"{a.Created}\",\"updatedAt\":\"{a.Updated}\",\"tagList\":{tags},\"favoritesCount\":{count},\"favorited\":{Bool(favorited)}," +
            $"\"author\":{Profile(a.Author, viewer)}}}";
    }

    public static string Page(IEnumerable<long> ids, long viewer, int count) =>
        "{\"articles\":[" + string.Join(",", ids.Select(i => Article(i, viewer, body: false))) + $"],\"articlesCount\":{count}}}";
}
