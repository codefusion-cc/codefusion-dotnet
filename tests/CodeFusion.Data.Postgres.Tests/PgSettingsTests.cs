using System;
using System.Collections.Generic;
using System.Linq;
using CodeFusion.Data.Postgres;
using FsCheck;
using FsCheck.Fluent;
using Npgsql;
using Xunit;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>Connection settings: both connection-string forms, what each refuses, and the startup parameters.</summary>
public sealed class PgSettingsTests
{
    [Fact]
    public void AUrlGivesHostPortDatabaseUserAndPasswordUnescaped()
    {
        var s = PgSettings.Parse("postgres://al%40ice:p%3Ass@db.example:6543/my%20db?sslmode=disable&application_name=x");
        Assert.Equal(("db.example", 6543, "my db", "al@ice", "p:ss"), (s.Host, s.Port, s.Database, s.User, s.Password));
    }

    [Fact]
    public void AUrlWithoutPortPasswordOrDatabaseKeepsTheDefaults()
    {
        var s = PgSettings.Parse("postgresql://bob@[::1]/");
        Assert.Equal(("::1", 5432, "bob", "bob", (string?)null), (s.Host, s.Port, s.Database, s.User, s.Password));
    }

    [Fact]
    public void KeyValuePairsTakeNpgsqlsNamesAndAliases()
    {
        var s = PgSettings.Parse("Host=h; Port=6000; Database=d; Username=u; Password=p; Search Path=s1");
        Assert.Equal(("h", 6000, "d", "u", "p"), (s.Host, s.Port, s.Database, s.User, s.Password));
        Assert.Equal([new("search_path", "s1")], s.Parameters);
        var alias = PgSettings.Parse("server=h2;DB=d2;User Id=u2;pwd=p2");
        Assert.Equal(("h2", "d2", "u2", "p2"), (alias.Host, alias.Database, alias.User, alias.Password));
        Assert.Equal("u3", PgSettings.Parse("user=u3").Database); // the user's database, as Postgres does
    }

    [Theory]
    [InlineData("Username=u;sslmode=Disable")]
    [InlineData("Username=u;sslmode=allow")]
    [InlineData("Username=u;SSL Mode=prefer")]
    [InlineData("postgres://u@h/d?sslmode=PREFER")]
    [InlineData("postgres://u@h/d?flag&sslmode=disable")] // a query part without a value is ignored
    public void AnSslModeThatNeedsNoTlsIsAccepted(string s) => Assert.Equal("u", PgSettings.Parse(s).User);

    [Theory]
    [InlineData("Username=u;sslmode=require")]
    [InlineData("Username=u;SSL Mode=verify-full")]
    [InlineData("postgres://u@h/d?sslmode=require")]
    [InlineData("postgres://u@h/d?a=1&sslmode=verify-ca")]
    public void AnSslModeThatNeedsTlsIsRefusedRatherThanSentInTheClear(string s) =>
        Assert.Throws<FormatException>(() => PgSettings.Parse(s));

    [Theory]
    [InlineData("Username=u;SSL=true")]                       // Npgsql's old switch, and JDBC's
    [InlineData("Username=u;ssl=1")]
    [InlineData("Username=u;requiressl=1")]                   // libpq's old switch
    [InlineData("Username=u;SslMode=VerifyCA")]               // Npgsql's own spelling
    [InlineData("Username=u;SSL Mode=VerifyFull")]
    [InlineData("Username=u;sslmode=bogus")]
    [InlineData("Username=u;SSL Negotiation=Direct")]         // TLS from the first byte
    [InlineData("Username=u;GSS Encryption Mode=Require")]    // encryption this client doesn't speak either
    [InlineData("Username=u;sslrootcert=system")]             // libpq: verify-full unless sslmode says otherwise
    [InlineData("postgres://u@h/d?requiressl=1")]
    [InlineData("postgres://u@h/d?ssl=true")]
    [InlineData("postgres://u@h/d?sslnegotiation=direct")]
    [InlineData("postgres://u@h/d?gssencmode=require")]
    [InlineData("postgres://u@h/d?sslrootcert=system")]
    public void AnyKeyThatAsksForTlsIsRefusedRatherThanIgnored(string s)
    {
        var e = Assert.Throws<FormatException>(() => PgSettings.Parse(s));
        Assert.Contains("TLS", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Username=u;SSL=false")]
    [InlineData("Username=u;requiressl=0")]
    [InlineData("Username=u;SSL Negotiation=Postgres")]
    [InlineData("Username=u;GSS Encryption Mode=Disable")]
    [InlineData("Username=u;gssencmode=prefer")]
    [InlineData("Username=u;sslrootcert=/etc/ca.pem")]
    [InlineData("Username=u;sslrootcert=system;sslmode=disable")]
    [InlineData("postgres://u@h/d?ssl=false&sslnegotiation=postgres")]
    public void KeysAboutTlsThatDontAskForItAreAccepted(string s) => Assert.Equal("u", PgSettings.Parse(s).User);

    [Theory]
    [InlineData("Password=\"pa;ss\"", "pa;ss")]
    [InlineData("Password='pa;ss'", "pa;ss")]
    [InlineData("Password=\"a\"\"b\"", "a\"b")]
    [InlineData("Password='a''b'", "a'b")]
    [InlineData("Password=\"a'b\"", "a'b")]
    [InlineData("Password='a\"b'", "a\"b")]
    [InlineData("Password=a\"b", "a\"b")]                 // a quote inside an unquoted value is a quote
    [InlineData("Password=  spaced  ", "spaced")]
    [InlineData("Password=\" spaced \" ", " spaced ")]
    [InlineData("Password=a b", "a b")]
    [InlineData("Password=a=b==c", "a=b==c")]
    [InlineData("Password=", null)]          // an empty value is no value, as in ADO.NET
    [InlineData("Password=;", null)]
    [InlineData("Password=\"\"", "")]          // a quoted empty one is empty
    [InlineData(" ;; Password = x ; ", "x")]
    [InlineData("Password=\"p\u00E4\u20AC\"", "p\u00E4\u20AC")]
    public void AValueIsQuotedAndEscapedAsAdoNetAndNpgsqlRead(string part, string? password) =>
        Assert.Equal(password, PgSettings.Parse("Username=u;" + part).Password);

    [Theory]
    [InlineData("Password=\"pa;ss")]        // never closed
    [InlineData("Password='pa;ss")]
    [InlineData("Password=\"a\"b")]         // more after the closing quote
    [InlineData("Password=ab\"")]           // an unquoted value can't end in a quote
    [InlineData("Password=ab'")]
    [InlineData("=x")]                      // no key
    public void AMalformedQuoteIsRefused(string part) =>
        Assert.Throws<FormatException>(() => PgSettings.Parse("Username=u;" + part));

    /// <summary>Text with every character the quoting rules care about.</summary>
    static Arbitrary<string> Values() =>
        Gen.Elements("a", "b", " ", ";", "=", "\"", "'", "\u00E4", "\t", "x y").ListOf().Select(l => string.Concat(l)).ToArbitrary();

    [Fact]
    public void AnyValueNpgsqlWritesIsReadBackExactly() =>
        Prop.ForAll(Values(), Values(), Values(), (password, user, database) =>
        {
            if (user.Trim().Length == 0) return true; // no user: refused, as it must be
            string cs = new NpgsqlConnectionStringBuilder { Host = "h", Username = user, Password = password, Database = database }.ConnectionString;
            var p = PgSettings.Parse(cs);
            return (p.Password ?? "") == password && p.User == user && p.Database == (database.Length == 0 ? user : database);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void AnyStringOfTheseKeysIsReadAsNpgsqlReadsItOrRefusedWhereItRefusesIt() =>
        Prop.ForAll(Gen.Elements("Username=", "Password=", "Database=", "Password = ", "Username= ").SelectMany(k => Values().Generator.Select(v => k + v))
            .ListOf().Select(l => string.Join(";", l) + ";Username=u0").ToArbitrary(), cs =>
        {
            NpgsqlConnectionStringBuilder reference;
            try
            {
                reference = new NpgsqlConnectionStringBuilder(cs);
            }
            catch (Exception e) when (!e.Message.StartsWith("Format of the initialization string", StringComparison.Ordinal))
            {
                return true; // a key Npgsql doesn't know (a key may hold an escaped = and a ;), which this client ignores
            }
            catch (ArgumentException)
            {
                try
                {
                    PgSettings.Parse(cs);
                    return false;
                }
                catch (FormatException)
                {
                    return true;
                }
            }
            var p = PgSettings.Parse(cs);
            return p.Password == reference.Password && p.User == reference.Username && p.Database == (string.IsNullOrEmpty(reference.Database) ? p.User : reference.Database);
        }).QuickCheckThrowOnFailure();

    [Theory]
    [InlineData("Host=h")]                     // no user
    [InlineData("postgres://h/d")]
    [InlineData("Username=u;Password")]        // not key=value (the part may be a password: not echoed)
    [InlineData("Username=u;Port=0")]
    [InlineData("Username=u;Port=65536")]
    [InlineData("Username=u;Port=-1")]
    [InlineData("Username=u;Port=2147483648")]  // found by fuzzing: past an int it was an OverflowException
    [InlineData("Username=u;Port=x")]
    public void MalformedSettingsAreRefused(string s) => Assert.Throws<FormatException>(() => PgSettings.Parse(s));

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void ThePortsEdgesAreAccepted(int port) => Assert.Equal(port, PgSettings.Parse($"Username=u;Port={port}").Port);

    [Fact]
    public void SetReplacesAParameterInPlace()
    {
        var s = new PgSettings().Set("a", "1").Set("b", "2").Set("a", "3");
        Assert.Equal([new("a", "3"), new("b", "2")], s.Parameters);
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("a\0b", "v")]
    [InlineData("a", "v\0")]
    public void SetRefusesAnEmptyNameAndNul(string name, string value) =>
        Assert.Throws<ArgumentException>(() => new PgSettings().Set(name, value));

    [Fact]
    public void ACloneCopiesEverySettingAndHasItsOwnParameters()
    {
        Func<PgSettings, PgTransport> transport = _ => throw new InvalidOperationException();
        var s = new PgSettings
        {
            Host = "h", Port = 7000, Database = "d", User = "u", Password = "p", ReceiveBufferSize = 4096,
            ConnectTimeout = TimeSpan.FromSeconds(3), ReconnectDelay = TimeSpan.FromSeconds(4), MaxReconnectDelay = TimeSpan.FromSeconds(5),
            AllowWeakPasswordAuth = true, Transport = transport,
        }.Set("search_path", "s");
        var c = s.Clone();
        Assert.Equal((s.Host, s.Port, s.Database, s.User, s.Password, s.ReceiveBufferSize, s.ConnectTimeout, s.ReconnectDelay, s.MaxReconnectDelay, s.AllowWeakPasswordAuth),
            (c.Host, c.Port, c.Database, c.User, c.Password, c.ReceiveBufferSize, c.ConnectTimeout, c.ReconnectDelay, c.MaxReconnectDelay, c.AllowWeakPasswordAuth));
        Assert.Same(transport, c.Transport);
        c.Set("lock_timeout", "1s");
        Assert.Equal([new KeyValuePair<string, string>("search_path", "s")], s.Parameters);
        Assert.Equal(2, c.Parameters.Count);
    }
}
