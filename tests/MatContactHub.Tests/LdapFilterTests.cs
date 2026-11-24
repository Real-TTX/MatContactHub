using MatContactHub.Services.Ldap;

namespace MatContactHub.Tests;

/// <summary>Filters: the string form (RFC 4515) and the evaluation on the entries (three-valued, without case, by alias, numbers as numbers).</summary>
public sealed class LdapFilterTests
{
    private static readonly LdapDirectory Directory = LdapTestData.Directory();

    private static LdapEntry Contact(Guid token) => Directory.Find(LdapDn.Normalize($"uid={token},ou={(token == LdapTestData.Ann ? "staff" : "customers")},{LdapTestData.Base}")!)!;

    private static bool Match(string filter, Guid token) => LdapFilterEvaluator.Matches(LdapFilter.Parse(filter), Contact(token), Directory.Phone);

    [Theory]
    [InlineData("(cn=Ann Example)")]
    [InlineData("(&(objectClass=person)(|(cn=Ann*)(sn=*ex*mp*le)))")]
    [InlineData("(!(title=boss))")]
    [InlineData("(givenName>=a)")]
    [InlineData("(givenName<=z)")]
    [InlineData("(telephoneNumber=*)")]
    [InlineData("(cn~=ann)")]
    [InlineData("(cn:caseExactMatch:=Ann)")]
    [InlineData("(:dn:2.5.13.5:=x)")]
    [InlineData("(cn=a\\2ab\\28c\\29d\\5ce)")]
    [InlineData("(&)")]
    [InlineData("(|)")]
    public void The_string_form_survives_a_round_trip(string text) => Assert.Equal(text, LdapFilter.Parse(text).ToString());

    [Fact]
    public void Escapes_and_UTF8_are_read()
    {
        var f = Assert.IsType<LdapFilter.Equality>(LdapFilter.Parse("(sn=M\\c3\\bcller \\2a)"));
        Assert.Equal("Müller *", f.Value);
        Assert.IsType<LdapFilter.Equality>(LdapFilter.Parse("cn=Ann"));
        var s = Assert.IsType<LdapFilter.Substrings>(LdapFilter.Parse("(cn=a*b*c*d)"));
        Assert.Equal("a", s.Initial);
        Assert.Equal(["b", "c"], s.Any);
        Assert.Equal("d", s.Final);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(")]
    [InlineData("(cn=a")]
    [InlineData("(=a)")]
    [InlineData("(cn=a\\2)")]
    [InlineData("(&(cn=a)")]
    [InlineData("(cn=a))")]
    [InlineData("(c n=a)")]
    public void Broken_filters_are_refused(string text) => Assert.Null(LdapFilter.TryParse(text));

    [Fact]
    public void Attribute_names_and_values_do_not_depend_on_case()
    {
        Assert.True(Match("(CN=ann example)", LdapTestData.Ann));
        Assert.True(Match("(commonName=ANN EXAMPLE)", LdapTestData.Ann));
        Assert.True(Match("(2.5.4.3=Ann Example)", LdapTestData.Ann));
        Assert.True(Match("(cn;lang-de=Ann Example)", LdapTestData.Ann));
        Assert.True(Match("(cn=  Ann   Example )", LdapTestData.Ann));
        Assert.True(Match("(OBJECTCLASS=InetOrgPerson)", LdapTestData.Ann));
        Assert.True(Match("(sn=müller)", LdapTestData.Carla));
        Assert.True(Match("(SN=MÜLLER)", LdapTestData.Carla));
        Assert.False(Match("(cn=Ann)", LdapTestData.Ann));
    }

    [Fact]
    public void Substrings_and_ordering_work_without_case()
    {
        Assert.True(Match("(cn=ann*)", LdapTestData.Ann));
        Assert.True(Match("(cn=*EXAMPLE)", LdapTestData.Ann));
        Assert.True(Match("(cn=*n*xa*)", LdapTestData.Ann));
        Assert.False(Match("(cn=*xa*n)", LdapTestData.Ann));
        Assert.True(Match("(mail=*@example.com)", LdapTestData.Ann));
        Assert.True(Match("(sn>=d)", LdapTestData.Ann));
        Assert.False(Match("(sn<=d)", LdapTestData.Ann));
        Assert.True(Match("(sn<=EXAMPLE)", LdapTestData.Ann));
    }

    [Fact]
    public void Logic_is_three_valued()
    {
        // an unknown attribute is Undefined: neither it nor its negation matches
        Assert.False(Match("(foo=bar)", LdapTestData.Ann));
        Assert.False(Match("(!(foo=bar))", LdapTestData.Ann));
        Assert.True(Match("(|(foo=bar)(cn=Ann Example))", LdapTestData.Ann));
        Assert.False(Match("(&(foo=bar)(cn=Ann Example))", LdapTestData.Ann));

        // a known attribute the entry does not have is False: its negation matches
        Assert.False(Match("(title=boss)", LdapTestData.Ann));
        Assert.True(Match("(!(title=boss))", LdapTestData.Ann));
        Assert.True(Match("(&)", LdapTestData.Ann));
        Assert.False(Match("(|)", LdapTestData.Ann));
    }

    [Fact]
    public void Presence_and_approximate_match()
    {
        Assert.True(Match("(objectClass=*)", LdapTestData.Ann));
        Assert.True(Match("(mobile=*)", LdapTestData.Ann));
        Assert.False(Match("(mobile=*)", LdapTestData.Bob));
        Assert.True(Match("(sn~=Muller)", LdapTestData.Carla));
        Assert.True(Match("(cn~=annexample)", LdapTestData.Ann));
    }

    [Fact]
    public void An_extensible_match_with_a_rule_matches_nothing_without_one_it_is_equality()
    {
        Assert.False(Match("(cn:caseExactMatch:=Ann Example)", LdapTestData.Ann));
        Assert.False(Match("(!(cn:caseExactMatch:=Ann Example))", LdapTestData.Ann));
        Assert.False(Match("(cn:dn:=Ann Example)", LdapTestData.Ann));
        Assert.True(Match("(cn:=ann example)", LdapTestData.Ann));
    }

    [Fact]
    public void The_attributes_of_a_contact_are_mapped()
    {
        var ann = Contact(LdapTestData.Ann);
        Assert.Equal("Ann Example", ann.First("cn"));
        Assert.Equal("Example", ann.First("sn"));
        Assert.Equal("Ann", ann.First("givenName"));
        Assert.Equal("Example GmbH", ann.First("o"));
        Assert.Equal("Sales", ann.First("ou"));
        Assert.Equal("Berlin", ann.First("l"));
        Assert.Equal("DE", ann.First("c"));
        Assert.Null(ann.First("co"));
        Assert.Equal(LdapTestData.Ann.ToString(), ann.First("entryUUID"));
        Assert.Equal(LdapTestData.Ann.ToString(), ann.First("uid"));
        Assert.Equal(["top", "person", "organizationalPerson", "inetOrgPerson"], ann.Get("objectClass")!.Values);
        Assert.Equal("Germany", Contact(LdapTestData.Bob).First("co"));
        Assert.Null(Contact(LdapTestData.Bob).First("c"));
    }
}
