using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CodeFusion.Http.Tests.Reference;

namespace CodeFusion.Http.Tests.Conduit;

/// <summary>The contest server's error bodies as ErrorReplies, each the same object every time, so a test can tell
/// which one a binder returned.</summary>
public static class ConduitErrors
{
    public static readonly ErrorReply InvalidBody = new(422, ContestErrors.InvalidBody);
    public static readonly ErrorReply RegisterFields = new(422, ContestErrors.RegisterFields);
    public static readonly ErrorReply LoginFields = new(422, ContestErrors.LoginFields);
    public static readonly ErrorReply UserFields = new(422, ContestErrors.UserFields);
    public static readonly ErrorReply ArticleFields = new(422, ContestErrors.ArticleFields);
    public static readonly ErrorReply ArticleUpdateFields = new(422, ContestErrors.ArticleUpdateFields);
    public static readonly ErrorReply BadTagList = new(422, ContestErrors.BadTagList);
    public static readonly ErrorReply CommentFields = new(422, ContestErrors.CommentFields);
    public static readonly ErrorReply BadPage = new(422, ContestErrors.BadPage);

    public static string Name(ErrorReply? e) =>
        e is null ? "ok"
        : ReferenceEquals(e, InvalidBody) ? "invalid body"
        : ReferenceEquals(e, RegisterFields) ? "register fields"
        : ReferenceEquals(e, LoginFields) ? "login fields"
        : ReferenceEquals(e, UserFields) ? "user fields"
        : ReferenceEquals(e, ArticleFields) ? "article fields"
        : ReferenceEquals(e, ArticleUpdateFields) ? "article update fields"
        : ReferenceEquals(e, BadTagList) ? "bad tag list"
        : ReferenceEquals(e, CommentFields) ? "comment fields"
        : ReferenceEquals(e, BadPage) ? "bad page"
        : "another reply";
}

// The contest's six bodies, written as body structs. The checks each endpoint made by hand become the field types:
// Utf8 is required (present and non-empty), Utf8? optional, [MinLength(1)] non-empty when sent.

[JsonEnvelope("user")]
public partial struct RegisterBody
{
    public Utf8 Username;
    public Utf8 Email;
    public Utf8 Password;
    static ErrorReply Invalid => ConduitErrors.RegisterFields;
}

[JsonEnvelope("user")]
public partial struct LoginBody
{
    public Utf8 Email { get; set; }
    public Utf8 Password { get; set; }
    static ErrorReply Invalid => ConduitErrors.LoginFields;
}

[JsonEnvelope("user")]
public partial struct UpdateUserBody
{
    [MinLength(1)] public Utf8? Email;
    [MinLength(1)] public Utf8? Username;
    [MinLength(1)] public Utf8? Password;
    public Utf8? Bio;
    public Utf8? Image;
    static readonly ErrorReply Invalid = ConduitErrors.UserFields;
}

[JsonEnvelope("article")]
public partial struct CreateArticleBody
{
    public Utf8 Title;
    public Utf8 Description;
    public Utf8 Body;
    [BodyError(nameof(BadTagList))] public Utf8List TagList;
    static ErrorReply Invalid => ConduitErrors.ArticleFields;
    static ErrorReply BadTagList => ConduitErrors.BadTagList;
}

[JsonEnvelope("article")]
public partial struct UpdateArticleBody
{
    [MinLength(1)] public Utf8? Title;
    [MinLength(1)] public Utf8? Description;
    [MinLength(1), JsonPropertyName("body")] public Utf8? Text;
    static ErrorReply Invalid => ConduitErrors.ArticleUpdateFields;
}

/// <summary>The contest answered a malformed comment body with the same reply as a missing field.</summary>
[JsonEnvelope("comment")]
public partial struct CommentBody
{
    public Utf8 Body;
    static ErrorReply Invalid => ConduitErrors.CommentFields;
    static ErrorReply InvalidBody => ConduitErrors.CommentFields;
}

/// <summary>GET /api/articles: TryPage's limit and offset, then the filters.</summary>
[FromQuery]
public partial struct ArticleListQuery
{
    public Utf8 Tag;
    public Utf8 Author;
    public Utf8 Favorited;
    [DefaultValue(20), Range(1, 100)] public long Limit;
    public long Offset;
    static ErrorReply Invalid => ConduitErrors.BadPage;
}
