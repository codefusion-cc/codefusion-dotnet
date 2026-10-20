using System;
using System.Security.Cryptography;
using System.Text;

namespace CodeFusion.Data.Postgres;

static class PgMd5
{
    /// <summary>AuthenticationMD5Password's answer: "md5" + md5hex(md5hex(password + user) + salt), NUL-terminated.</summary>
    public static byte[] Response(string user, string password, ReadOnlySpan<byte> salt)
    {
        if (salt.Length != 4) throw new PgConnectionException("malformed md5 salt");
        // password and user encoded side by side: a concatenated string would be a copy of the password nothing wipes
        int p = Encoding.UTF8.GetByteCount(password);
        byte[] secret = new byte[p + Encoding.UTF8.GetByteCount(user)];
        Encoding.UTF8.GetBytes(password, secret);
        Encoding.UTF8.GetBytes(user, secret.AsSpan(p));
        Span<byte> hash = stackalloc byte[16];
        // Postgres's md5 password exchange, answered only with AllowWeakPasswordAuth.
#pragma warning disable CA5351
        MD5.HashData(secret, hash);
        CryptographicOperations.ZeroMemory(secret);
        Span<byte> salted = stackalloc byte[36];
        Hex(hash, salted);
        salt.CopyTo(salted[32..]);
        MD5.HashData(salted, hash);
#pragma warning restore CA5351
        CryptographicOperations.ZeroMemory(salted);
        var response = new byte[3 + 32 + 1];
        "md5"u8.CopyTo(response);
        Hex(hash, response.AsSpan(3));
        return response;
    }

    static void Hex(ReadOnlySpan<byte> bytes, Span<byte> into)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            into[2 * i] = (byte)"0123456789abcdef"[bytes[i] >> 4];
            into[2 * i + 1] = (byte)"0123456789abcdef"[bytes[i] & 15];
        }
    }
}

/// <summary>
/// SCRAM-SHA-256 (RFC 5802, RFC 7677) without channel binding (there is no TLS), as Postgres runs it. The server
/// must prove it knows the password too: its signature is checked before AuthenticationOk counts. The password goes
/// through SASLprep first (<see cref="PgSaslPrep"/>).
/// </summary>
sealed class PgScram : IDisposable
{
    /// <summary>Postgres's own count. Fewer make the proof a server posing as Postgres collects cheaper to crack, so
    /// they are refused unless weak password authentication is allowed.</summary>
    internal const int MinIterations = 4096;
    /// <summary>A server asking for far more than Postgres's 4096 only makes connecting slow, so it is refused.</summary>
    internal const int MaxIterations = 1 << 20;

    readonly byte[] _password;
    readonly string _clientFirstBare;
    readonly string _nonce;
    readonly int _minIterations;
    byte[]? _serverSignature;

    /// <summary>The server's signature checked out.</summary>
    public bool Verified { get; private set; }

    /// <param name="password">The user's password, prepared with SASLprep.</param>
    /// <param name="user">Postgres takes the user from the startup packet and ignores this one; the RFC's test vector needs it.</param>
    /// <param name="nonce">Fixed only for test vectors; otherwise 18 random bytes.</param>
    /// <param name="minIterations">The fewest iterations taken: <see cref="MinIterations"/>, or 1 where weak password authentication is allowed.</param>
    public PgScram(string password, string user = "", string? nonce = null, int minIterations = MinIterations)
        : this(PgSaslPrep.Prepare(password), user, nonce, minIterations) { }

    /// <param name="password">The bytes hashed, owned and wiped by this exchange once the proof is computed.</param>
    /// <param name="user">As above.</param>
    /// <param name="nonce">As above.</param>
    /// <param name="minIterations">As above.</param>
    public PgScram(byte[] password, string user, string? nonce, int minIterations)
    {
        _password = password;
        _minIterations = minIterations;
        _nonce = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        _clientFirstBare = $"n={user},r={_nonce}";
    }

    /// <summary>Wipes the password's bytes, should the exchange end before the proof.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_password);

    /// <summary>The client-first-message: no channel binding ("n,,"), our nonce.</summary>
    public byte[] ClientFirst() => Encoding.UTF8.GetBytes("n,," + _clientFirstBare);

    /// <summary>The client-final-message with our proof, for the server-first-message.</summary>
    public byte[] ClientFinal(ReadOnlySpan<byte> serverFirstBytes)
    {
        if (_serverSignature is not null) throw new PgConnectionException("a second SCRAM server-first-message");
        try
        {
            return Final(serverFirstBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_password); // used once, whatever the server sent
        }
    }

    byte[] Final(ReadOnlySpan<byte> serverFirstBytes)
    {
        string serverFirst = Ascii(serverFirstBytes);
        string[] parts = serverFirst.Split(',');
        // r=nonce,s=salt,i=iterations, in that order; a mandatory extension (m=) comes first and is refused
        if (parts.Length < 3 || !parts[0].StartsWith("r=", StringComparison.Ordinal) || !parts[1].StartsWith("s=", StringComparison.Ordinal)
            || !parts[2].StartsWith("i=", StringComparison.Ordinal))
            throw new PgConnectionException("malformed SCRAM server-first-message");
        string nonce = parts[0][2..];
        if (nonce.Length <= _nonce.Length || !nonce.StartsWith(_nonce, StringComparison.Ordinal))
            throw new PgConnectionException("the SCRAM server nonce doesn't extend ours");
        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(parts[1][2..]);
        }
        catch (FormatException)
        {
            throw new PgConnectionException("malformed SCRAM salt");
        }
        if (!int.TryParse(parts[2].AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int iterations)
            || iterations is < 1 or > MaxIterations)
            throw new PgConnectionException("SCRAM iteration count out of range");
        if (iterations < _minIterations)
            throw new PgConnectionException($"the server asks for {iterations} SCRAM iterations, fewer than Postgres's {MinIterations}; set AllowWeakPasswordAuth to allow it");

        string withoutProof = "c=biws,r=" + nonce; // biws = base64("n,,")
        byte[] authMessage = Encoding.ASCII.GetBytes(_clientFirstBare + "," + serverFirst + "," + withoutProof);
        Span<byte> salted = stackalloc byte[32], clientKey = stackalloc byte[32], storedKey = stackalloc byte[32], proof = stackalloc byte[32];
        Rfc2898DeriveBytes.Pbkdf2(_password, salt, salted, iterations, HashAlgorithmName.SHA256);
        HMACSHA256.HashData(salted, "Client Key"u8, clientKey);
        SHA256.HashData(clientKey, storedKey);
        HMACSHA256.HashData(storedKey, authMessage, proof);
        for (int i = 0; i < 32; i++) proof[i] ^= clientKey[i];
        Span<byte> serverKey = stackalloc byte[32];
        HMACSHA256.HashData(salted, "Server Key"u8, serverKey);
        _serverSignature = HMACSHA256.HashData(serverKey, authMessage);
        string final = withoutProof + ",p=" + Convert.ToBase64String(proof);
        // every key, and the proof: it goes out in the clear, but with the stored key it gives the client key back
        CryptographicOperations.ZeroMemory(salted);
        CryptographicOperations.ZeroMemory(clientKey);
        CryptographicOperations.ZeroMemory(storedKey);
        CryptographicOperations.ZeroMemory(proof);
        CryptographicOperations.ZeroMemory(serverKey);
        return Encoding.ASCII.GetBytes(final);
    }

    /// <summary>Checks the server-final-message's signature: the server proves it knows the password.</summary>
    public void VerifyServerFinal(ReadOnlySpan<byte> serverFinalBytes)
    {
        string serverFinal = Ascii(serverFinalBytes);
        if (serverFinal.StartsWith("e=", StringComparison.Ordinal)) throw new PgConnectionException("SCRAM: the server refused: " + serverFinal.Split(',')[0][2..]);
        if (_serverSignature is null) throw new PgConnectionException("SCRAM server-final-message before our proof");
        string verifier = serverFinal.Split(',')[0];
        byte[] signature;
        try
        {
            signature = verifier.StartsWith("v=", StringComparison.Ordinal) ? Convert.FromBase64String(verifier[2..]) : [];
        }
        catch (FormatException)
        {
            signature = [];
        }
        if (!CryptographicOperations.FixedTimeEquals(signature, _serverSignature)) throw new PgConnectionException("the SCRAM server signature doesn't match: the server doesn't know the password");
        Verified = true;
    }

    /// <summary>SCRAM's messages are printable ASCII; anything else is refused before it gets parsed.</summary>
    static string Ascii(ReadOnlySpan<byte> b)
    {
        foreach (byte c in b)
            if (c is < 0x20 or > 0x7e) throw new PgConnectionException("malformed SCRAM message");
        return Encoding.ASCII.GetString(b);
    }
}
