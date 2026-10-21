using System;
using CodeFusion.Data.Postgres;
using Xunit;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>A null where the API takes none fails at once with its parameter's name, not later as a
/// NullReferenceException inside the client.</summary>
public class NullArgumentTests
{
    [Fact]
    public void SettingsRefuseANullStringOrParameter()
    {
        Assert.Equal("s", Assert.Throws<ArgumentNullException>(() => PgSettings.Parse(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => new PgSettings().Set(null!, "x")).ParamName);
        Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => new PgSettings().Set("search_path", null!)).ParamName);
    }

    [Fact]
    public void AStatementRefusesNullSql() =>
        Assert.Equal("sql", Assert.Throws<ArgumentNullException>(() => new PgStatement(null!)).ParamName);
}
