using MatContactHub.Localization;

namespace MatContactHub.Tests;

/// <summary>The texts of the LDAP pages that are made of a name and a value at run time exist in the catalog.</summary>
public sealed class LdapTextTests
{
    [Fact]
    public void The_keys_of_the_tabs_and_the_filters_exist()
    {
        var keys = Pages.Admin.Ldap.IndexModel.Tabs.Select(t => "Ldap.Tab." + t)
            .Concat(new[] { "ok", "empty", "error", "bind" }.Select(o => "Ldap.Log.Outcome." + o))
            .Concat(Enum.GetNames<Data.NumberFormat>().SelectMany(f => new[] { "Ldap.NumberFormat." + f, "Ldap.NumberFormat." + f + ".Example" }));
        Assert.All(keys, k => Assert.True(TextCatalog.Entries.ContainsKey(k), k));
    }
}
