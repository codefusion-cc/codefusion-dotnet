using System;
using System.Collections.Generic;
using System.Data.Common;

namespace CodeFusion.Data.Postgres;

/// <summary>Where and as whom the loops connect, and the session settings every connection starts with.</summary>
public sealed class PgSettings
{
    /// <summary>The server's host name or address.</summary>
    public string Host = "127.0.0.1";
    /// <summary>The server's port.</summary>
    public int Port = 5432;
    /// <summary>The database to connect to.</summary>
    public string Database = "";
    /// <summary>The user to log in as.</summary>
    public string User = "";
    /// <summary>The password, for SCRAM (or, with <see cref="AllowWeakPasswordAuth"/>, cleartext and md5). A string:
    /// it stays in memory as long as the settings do, since every new connection logs in with it.</summary>
    public string? Password;
    /// <summary>Run-time parameters sent in the startup packet, so a connection needs no SET round trip:
    /// <c>search_path</c>, <c>statement_timeout</c>, <c>plan_cache_mode</c> and the like.</summary>
    // A list the app fills before the loops connect, like every other field here; nothing derives from it.
#pragma warning disable CA1002
    public readonly List<KeyValuePair<string, string>> Parameters = [];
#pragma warning restore CA1002
    /// <summary>A whole read group's replies (32 list pages are about 600 KB) arrive in few receives.</summary>
    public int ReceiveBufferSize = 256 << 10;
    /// <summary>How long connecting may take, name lookup and authentication included.</summary>
    public TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>After a failed connect a loop waits this long before it tries again, twice as long after each
    /// further failure, up to <see cref="MaxReconnectDelay"/> (each wait a quarter shorter or longer at random, so
    /// the loops don't all try at once). Meanwhile its commands fail at once (<see cref="PgStatus.Broken"/>) with
    /// the last failure, instead of each group trying to connect.</summary>
    public TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(100);
    /// <summary>The longest wait between connect attempts.</summary>
    public TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(2);
    /// <summary>The connections a reader or writer opens for statements that run alone (<see cref="PgStatement.RunsAlone"/>),
    /// at the first such command: each runs one command at a time, and the next command goes to one that is free, so
    /// a slow report holds up only its own connection. They come on top of the loops' own.</summary>
    public int AloneConnections = 2;
    /// <summary>Answer a server that asks for a cleartext or md5 password or runs SCRAM with fewer than Postgres's
    /// 4096 iterations, and log in where a password is set but the server never asks for it (trust). Off: only SCRAM
    /// logs in (trust only without a password), so a server posing as Postgres can neither collect the password nor
    /// skip proving it knows it.</summary>
    public bool AllowWeakPasswordAuth;
    /// <summary>Opens the connection's byte stream instead of a TCP socket (tests).</summary>
    public Func<PgSettings, PgTransport>? Transport;

    /// <summary>Sets (or replaces) a startup parameter. Not user, database, client_encoding or replication: the
    /// client sets the first three itself (UTF-8 is the only encoding it speaks), and a replication session runs no
    /// ordinary statements.</summary>
    public PgSettings Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (Refused(name, value) is { } why) throw new ArgumentException(why);
        for (int i = 0; i < Parameters.Count; i++)
            if (Parameters[i].Key == name)
            {
                Parameters[i] = new(name, value);
                return this;
            }
        Parameters.Add(new(name, value));
        return this;
    }

    /// <summary>Why a startup parameter can't be sent, or null.</summary>
    internal static string? Refused(string name, string value) =>
        name.Length == 0 || name.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal)
            ? "a parameter can't be empty or hold NUL"
            : name.Equals("user", StringComparison.OrdinalIgnoreCase) || name.Equals("database", StringComparison.OrdinalIgnoreCase)
              || name.Equals("client_encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("replication", StringComparison.OrdinalIgnoreCase)
                ? $"the {name} parameter is the client's own"
                : null;

    /// <summary>A copy with its own parameter list (a writer's adds lock_timeout to the reader's).</summary>
    public PgSettings Clone()
    {
        var c = new PgSettings { Host = Host, Port = Port, Database = Database, User = User, Password = Password,
            ReceiveBufferSize = ReceiveBufferSize, ConnectTimeout = ConnectTimeout, ReconnectDelay = ReconnectDelay, MaxReconnectDelay = MaxReconnectDelay, AloneConnections = AloneConnections,
            AllowWeakPasswordAuth = AllowWeakPasswordAuth, Transport = Transport, Environment = Environment, MaxTakeIn = MaxTakeIn,
            _sslModeSet = _sslModeSet, _gssModeSet = _gssModeSet, _sslRootSystem = _sslRootSystem };
        c.Parameters.AddRange(Parameters);
        return c;
    }

    /// <summary>The most the replies taken in while a large batch goes out may fill: what one message may be. Tests
    /// lower it.</summary>
    internal int MaxTakeIn = PgConnection.MaxMessage;

    /// <summary>Where the TLS environment variables are read (tests replace it).</summary>
    internal Func<string, string?> Environment = System.Environment.GetEnvironmentVariable;
    /// <summary>The connection string said how to use TLS or GSS encryption (as none), so the PGSSLMODE family
    /// doesn't apply, as in libpq.</summary>
    bool _sslModeSet, _gssModeSet;

    /// <summary>
    /// A <c>postgres://user:password@host:port/db</c> URL (DATABASE_URL) or a <c>Host=..;Username=..</c> string as
    /// Npgsql takes it (the tests' CODEFUSION_TEST_DB), with its quoting: a value in single or double quotes, the quote
    /// doubled inside, holds semicolons and keeps its spaces. Keys this client has no use for are ignored, except one
    /// that asks for TLS or GSS encryption (an sslmode that needs it, ssl, requiressl, sslnegotiation=direct,
    /// sslrootcert=system, gssencmode=require, in libpq's or Npgsql's spelling): there is none, so that is refused
    /// rather than sent in the clear.
    /// </summary>
    public static PgSettings Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var p = new PgSettings();
        if (s.StartsWith("postgres://", StringComparison.Ordinal) || s.StartsWith("postgresql://", StringComparison.Ordinal))
        {
            var uri = new Uri(s);
            string[] userInfo = uri.UserInfo.Split(':', 2);
            p.Host = uri.Host.Trim('[', ']');
            if (uri.Port > 0) p.Port = uri.Port;
            p.Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            p.User = Uri.UnescapeDataString(userInfo[0]);
            p.Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null;
            foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                string key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]), value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
                p.Tls(key.ToLowerInvariant(), value);
            }
        }
        else
        {
            var pairs = new DbConnectionStringBuilder();
            try
            {
                pairs.ConnectionString = s;
            }
            catch (ArgumentException e)
            {
                throw new FormatException(e.Message); // where it broke, never the text: it may hold the password
            }
            foreach (KeyValuePair<string, object> pair in pairs)
            {
                string value = (string)pair.Value;
                switch (pair.Key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant())
                {
                    case "host": case "server": p.Host = value; break;
                    case "port":
                        // past an int is as malformed as not a number
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out p.Port))
                            throw new FormatException("the port is not a number");
                        break;
                    case "database": case "db": p.Database = value; break;
                    case "username": case "user": case "userid": p.User = value; break;
                    case "password": case "pwd": p.Password = value; break;
                    case "searchpath": p.Set("search_path", value); break;
                    case var key: p.Tls(key, value); break;
                }
            }
        }
        if (p._sslRootSystem && !p._sslModeSet) throw NoTls("sslrootcert=system");
        if (p.User.Length == 0) throw new FormatException("no user in the connection settings");
        if (p.Port is <= 0 or > 65535) throw new FormatException($"port {p.Port} is out of range");
        if (p.Database.Length == 0) p.Database = p.User;
        return p;
    }

    bool _sslRootSystem;

    /// <summary>A key about TLS or GSS encryption, as libpq or Npgsql (spaces removed, lower case) name it.</summary>
    void Tls(string key, string value)
    {
        switch (key)
        {
            case "sslmode":
                if (!value.Equals("disable", StringComparison.OrdinalIgnoreCase) && !value.Equals("allow", StringComparison.OrdinalIgnoreCase)
                    && !value.Equals("prefer", StringComparison.OrdinalIgnoreCase))
                    throw NoTls($"sslmode {value}");
                _sslModeSet = true;
                break;
            case "ssl" or "requiressl":
                if (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0") throw NoTls($"{key}={value}");
                break;
            case "sslnegotiation":
                if (value.Equals("direct", StringComparison.OrdinalIgnoreCase)) throw NoTls("sslnegotiation=direct");
                break;
            case "sslrootcert":
                _sslRootSystem = value == "system";
                break;
            case "gssencmode" or "gssencryptionmode":
                if (value.Equals("require", StringComparison.OrdinalIgnoreCase)) throw NoTls($"{key}={value}");
                _gssModeSet = true;
                break;
        }
    }

    static FormatException NoTls(string what) => new($"{what} needs TLS or GSS encryption, which this client doesn't speak");

    /// <summary>
    /// libpq's environment variables that ask for TLS or GSS encryption (PGSSLMODE, PGREQUIRESSL, PGSSLNEGOTIATION,
    /// PGSSLROOTCERT=system, PGGSSENCMODE), where the connection string didn't say otherwise: a deployment that set
    /// them for every client gets a refusal from this one, not a login in the clear. Checked before connecting over TCP.
    /// </summary>
    internal void CheckTlsEnvironment()
    {
        var env = new PgSettings();
        foreach (var (variable, key) in (ReadOnlySpan<(string, string)>)[("PGSSLMODE", "sslmode"), ("PGREQUIRESSL", "requiressl"),
                     ("PGSSLNEGOTIATION", "sslnegotiation"), ("PGSSLROOTCERT", "sslrootcert"), ("PGGSSENCMODE", "gssencmode")])
        {
            if (key == "gssencmode" ? _gssModeSet : _sslModeSet) continue;
            if (Environment(variable) is not { Length: > 0 } value) continue;
            try
            {
                env.Tls(key, value);
            }
            catch (FormatException e)
            {
                throw new PgConnectionException($"{variable}: {e.Message}");
            }
        }
        if (env._sslRootSystem && !_sslModeSet && !env._sslModeSet) throw new PgConnectionException("PGSSLROOTCERT=system needs TLS, which this client doesn't speak");
    }
}
