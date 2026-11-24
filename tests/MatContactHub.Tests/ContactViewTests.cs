using MatContactHub.Pages.Contacts;

namespace MatContactHub.Tests;

/// <summary>The numbers of the lists: a synced number in E.164 is shown in groups, a typed one as typed; the <c>tel:</c> link is the same.</summary>
public sealed class ContactViewTests
{
    [Theory]
    [InlineData("+49307654321", "+49 30 7654321")]
    [InlineData("+4533123456", "+45 33 12 34 56")]
    [InlineData("089 1234567", "089 1234567")]
    [InlineData("+49 30 1234", "+49 30 1234")]
    [InlineData("+", "+")]
    [InlineData(null, null)]
    public void A_number_in_E164_is_shown_in_groups(string? stored, string? shown)
    {
        Assert.Equal(shown, ContactView.Readable(stored));
    }

    [Fact]
    public void The_link_calls_the_stored_number()
    {
        Assert.Equal("<a href=\"tel:+49307654321\">+49 30 7654321</a>", ContactView.PhoneLink("+49307654321").ToString());
    }
}
