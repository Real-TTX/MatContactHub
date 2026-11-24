using MatContactHub.Localization;
using MatContactHub.Services;

namespace MatContactHub.Tests;

/// <summary>The words of the activity log are those of whoever reads it: a text of the catalog is stored as its key and its values and worded when shown.</summary>
public sealed class ActivityLogTests
{
    [Fact]
    public void A_text_is_worded_in_the_language_of_the_reader()
    {
        var created = ActivityLog.Text("Activity.Detail.Created");
        Assert.Equal("created", Languages.In("en", () => ActivityLog.Show(created)));
        Assert.Equal("angelegt", Languages.In("de", () => ActivityLog.Show(created)));

        var imported = ActivityLog.Text("Contacts.Import.Done", 2, 0, 1);
        Assert.Equal(Languages.In("de", () => Text.T("Contacts.Import.Done", 2, 0, 1)), Languages.In("de", () => ActivityLog.Show(imported)));
    }

    [Fact]
    public void A_value_may_be_a_text_itself_and_names_stay_as_they_are()
    {
        var summary = ActivityLog.Text("Sources.Run.Summary", ActivityLog.Text("Sources.State.Failed"), 1, 2, 0);
        Assert.Equal(Languages.In("de", () => Text.T("Sources.Run.Summary", Text.T("Sources.State.Failed"), 1, 2, 0)), Languages.In("de", () => ActivityLog.Show(summary)));

        var bind = ActivityLog.Text("Activity.Detail.FailedBind", "10.0.0.7");
        Assert.Equal("fehlgeschlagene Anmeldung von 10.0.0.7", Languages.In("de", () => ActivityLog.Show(bind)));
        Assert.Contains("Jürgen Größe", ActivityLog.Text("Activity.Detail.CertificateUploaded", "CN=Jürgen Größe"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Dr. Zoë M. Zzvcard")]
    [InlineData("{\"k\":\"Activity.Detail.Cre")] // cut at the length of the column
    public void Anything_else_is_shown_as_it_was_stored(string? stored) => Assert.Equal(stored, ActivityLog.Show(stored));
}
