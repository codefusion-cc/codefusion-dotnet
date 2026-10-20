using System.ComponentModel.DataAnnotations.Schema;
using CodeFusion;
using CodeFusion.Data;

namespace Conduit.Data;

// The contest's schema.sql as entities, by EF's conventions: snake_case names, AuthorId with Author, Many<T> for the
// other side. They describe the tables to the query generator and are never built.

public sealed class User
{
    public long Id { get; init; }
    public Utf8 Username { get; init; }
    public Utf8 Email { get; init; }
    public Utf8 PasswordHash { get; init; }
    public Utf8? Bio { get; init; }
    public Utf8? Image { get; init; }
    public Timestamp CreatedAt { get; init; }

    /// <summary>The follows naming this user as the one followed.</summary>
    [InverseProperty(nameof(Follow.Followee))]
    public Many<Follow> Followers { get; init; }
}

public sealed class Follow
{
    public long FollowerId { get; init; }
    public User Follower { get; init; } = null!;
    public long FolloweeId { get; init; }
    public User Followee { get; init; } = null!;
    public Timestamp CreatedAt { get; init; }
}

public sealed class Article
{
    public long Id { get; init; }
    public Utf8 Slug { get; init; }
    public long AuthorId { get; init; }
    public User Author { get; init; } = null!;
    public Utf8 Title { get; init; }
    public Utf8 Description { get; init; }
    public Utf8 Body { get; init; }
    public Timestamp CreatedAt { get; init; }
    public Timestamp UpdatedAt { get; init; }

    [JoinTable("article_tags")]
    public Many<Tag> Tags { get; init; }

    public Many<Favorite> Favorites { get; init; }

    public Many<Comment> Comments { get; init; }
}

public sealed class Tag
{
    public int Id { get; init; }
    public Utf8 Name { get; init; }
}

public sealed class ArticleTag
{
    public long ArticleId { get; init; }
    public Article Article { get; init; } = null!;
    public int TagId { get; init; }
    public Tag Tag { get; init; } = null!;
}

public sealed class Favorite
{
    public long UserId { get; init; }
    public User User { get; init; } = null!;
    public long ArticleId { get; init; }
    public Article Article { get; init; } = null!;
    public Timestamp CreatedAt { get; init; }
}

public sealed class Comment
{
    public long Id { get; init; }
    public long ArticleId { get; init; }
    public Article Article { get; init; } = null!;
    public long AuthorId { get; init; }
    public User Author { get; init; } = null!;
    public Utf8 Body { get; init; }
    public Timestamp CreatedAt { get; init; }
    public Timestamp UpdatedAt { get; init; }
}
