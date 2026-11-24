using MatContactHub.Data;
using MatContactHub.Services.Ldap;

namespace MatContactHub.Tests;

/// <summary>The telephone numbers in filters: a number is found however the telephone writes it (+49, 0049, 0, with spaces), equal or as a part of its digits.</summary>
public sealed class LdapPhoneTests
{
    private static readonly LdapDirectory Directory = LdapTestData.Directory();

    private static LdapEntry Contact(Guid token) => Directory.Find(LdapDn.Normalize($"uid={token},ou={(token == LdapTestData.Ann ? "staff" : "customers")},{LdapTestData.Base}")!)!;

    private static bool Match(string filter, Guid token) => LdapFilterEvaluator.Matches(LdapFilter.Parse(filter), Contact(token), Directory.Phone);

    [Theory]
    [InlineData("+49301234567")]
    [InlineData("+49 30 1234567")]
    [InlineData("0049301234567")]
    [InlineData("030 1234567")]
    [InlineData("0301234567")]
    [InlineData("030/123-4567")]
    public void Equality_finds_the_number_in_every_form(string number) => Assert.True(Match($"(telephoneNumber={number})", LdapTestData.Ann));

    [Theory]
    [InlineData("*1234567")]
    [InlineData("*301234567")]
    [InlineData("*0301234567")]
    [InlineData("*+49301234567")]
    [InlineData("*0049301234567")]
    [InlineData("*49301234567")]
    [InlineData("+4930*")]
    [InlineData("030*")]
    [InlineData("004930*")]
    [InlineData("*12345*")]
    [InlineData("*30 123*")]
    public void Substrings_find_the_number_on_digits(string pattern) => Assert.True(Match($"(telephoneNumber={pattern})", LdapTestData.Ann), pattern);

    [Theory]
    [InlineData("(telephoneNumber=+49301234568)")]
    [InlineData("(telephoneNumber=*1234568)")]
    [InlineData("(telephoneNumber=040*)")]
    [InlineData("(telephoneNumber=+4930123456)")]
    [InlineData("(telephoneNumber=+43301234567)")]
    [InlineData("(telephoneNumber=+41 30 1234567)")]
    [InlineData("(telephoneNumber=0043301234567)")]
    [InlineData("(mobile=*1234567)")]
    public void Other_numbers_do_not_match(string filter) => Assert.False(Match(filter, LdapTestData.Ann), filter);

    [Fact]
    public void A_number_stored_nationally_is_found_internationally_and_the_other_way_round()
    {
        // Bob has 030 9876543 as typed, Carla 0049 160 1112223 and an Austrian +43 1 5551234
        Assert.True(Match("(telephoneNumber=+49309876543)", LdapTestData.Bob));
        Assert.True(Match("(telephoneNumber=*+49309876543)", LdapTestData.Bob));
        Assert.True(Match("(telephoneNumber=*0309876543)", LdapTestData.Bob));
        Assert.True(Match("(mobile=01601112223)", LdapTestData.Carla));
        Assert.True(Match("(mobile=*1601112223)", LdapTestData.Carla));
        Assert.True(Match("(homePhone=0043 1 5551234)", LdapTestData.Carla));
        Assert.True(Match("(homePhone=*+4315551234)", LdapTestData.Carla));
    }

    [Fact]
    public void The_typical_filters_of_a_telephone_system_find_the_caller()
    {
        // STARFACE: (|(telephoneNumber=*%)(mobile=*%)) with % the number of the caller as it arrives
        foreach (var caller in new[] { "01712345678", "+491712345678", "00491712345678", "1712345678" })
        {
            Assert.True(Match($"(|(telephoneNumber=*{caller})(mobile=*{caller})(homePhone=*{caller}))", LdapTestData.Ann), caller);
        }
    }

    [Fact]
    public void The_readings_of_one_filter_are_limited_by_its_budget()
    {
        var phone = new LdapPhone("DE");
        var budget = new LdapPhoneBudget(5);

        // 030* is read as 030 and as 4930 (2), 01*02* in four ways (4 > 3 left): then as written only
        Assert.Equal(2, phone.Patterns("030", [], null, budget).Count);
        Assert.Equal(["01"], phone.Patterns("01", ["02"], null, budget).Select(p => p.Initial));
        Assert.Equal(2, phone.Patterns(null, [], "0301234567", budget).Count);

        // a crafted filter of hundreds of such parts stays near one pattern per part
        var crafted = "(|" + string.Concat(Enumerable.Repeat("(telephoneNumber=01*02*03*04*05*09)", 500)) + "(telephoneNumber=*0301234567))";
        Assert.True(Match(crafted, LdapTestData.Ann));
        Assert.False(Match("(|" + string.Concat(Enumerable.Repeat("(telephoneNumber=01*02*03*04*05*09)", 500)) + ")", LdapTestData.Ann));
    }

    [Fact]
    public void A_substring_without_digits_is_compared_as_text()
    {
        Assert.False(Match("(telephoneNumber=*abc*)", LdapTestData.Ann));
        Assert.True(Match("(telephoneNumber=*)", LdapTestData.Ann));
    }

    [Theory]
    [InlineData(NumberFormat.AsStored, "+49 30 1234567", "0171 2345678")]
    [InlineData(NumberFormat.E164, "+49301234567", "+491712345678")]
    [InlineData(NumberFormat.National, "030 1234567", "0171 2345678")]
    [InlineData(NumberFormat.InternationalDigits, "0049301234567", "00491712345678")]
    public void Numbers_are_written_in_the_format_of_the_settings(NumberFormat format, string business, string mobile)
    {
        var directory = LdapTestData.Directory(LdapTestData.Settings(format: format));
        var ann = directory.Find(LdapDn.Normalize($"uid={LdapTestData.Ann},ou=staff,{LdapTestData.Base}")!)!;
        Assert.Equal(business, ann.First("telephoneNumber"));
        Assert.Equal(mobile, ann.First("mobile"));

        // the format changes what is shown, never what matches
        Assert.True(LdapFilterEvaluator.Matches(LdapFilter.Parse("(telephoneNumber=*0301234567)"), ann, directory.Phone));
    }
}
