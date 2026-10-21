namespace CodeFusion.Http;

/// <summary>The request method, recognised once while parsing so handlers switch on a byte instead of comparing
/// text. Any other valid token is <see cref="Other"/>; its text is in <see cref="HttpExchange.MethodBytes"/>.</summary>
public enum RequestMethod : byte
{
    /// <summary>A method not listed here; <see cref="HttpExchange.MethodBytes"/> has it.</summary>
    Other,
    /// <summary>GET.</summary>
    Get,
    /// <summary>HEAD.</summary>
    Head,
    /// <summary>POST.</summary>
    Post,
    /// <summary>PUT.</summary>
    Put,
    /// <summary>DELETE.</summary>
    Delete,
    /// <summary>PATCH.</summary>
    Patch,
    /// <summary>OPTIONS.</summary>
    Options,
}
