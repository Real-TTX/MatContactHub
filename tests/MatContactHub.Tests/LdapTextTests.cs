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
            .Concat(Enum.GetNames<Data.NumberFormat>().Select(f => "Ldap.NumberFormat." + f));
        Assert.All(keys, k => Assert.True(TextCatalog.Entries.ContainsKey(k), k));
    }
}
