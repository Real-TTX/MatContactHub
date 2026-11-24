using System.Net;
using System.Text;
using MatContactHub.Services;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatContactHub.Controls;

/// <summary>
/// The one code base of tabs whose panels are parts of ONE page (one form: every field is sent with Save, whichever tab shows it), as the toolkit draws them
/// (<c>data-tabs</c>: buttons with <c>role=tab</c>, panels with <c>role=tabpanel</c>; the arrow keys, Home and End move between the tabs):
/// <c>&lt;md-panels id="contact" label="Sections" active="general"&gt;&lt;md-panel key="general" title="General" icon="user"&gt;…&lt;/md-panel&gt;&lt;/md-panels&gt;</c>.
/// The server chooses the tab that is open (<c>active</c>: the one of an error, or <c>?tab=</c>), which must be the key of one of its panels; the others are hidden.
/// A field with an error in a closed panel opens it when the form is checked (<c>js/contacts.js</c>). Several tab groups can stand on one page (their ids differ).
/// Tabs that are pages of their own are <see cref="TabsTagHelper"/>.
/// </summary>
[HtmlTargetElement("md-panels")]
public sealed class PanelTabsTagHelper : TagHelper
{
    internal const string ItemsKey = "md-panels";

    /// <summary>The id of the group: the ids of its tabs and panels start with it.</summary>
    [HtmlAttributeName("id")] public string Id { get; set; } = "tabs";

    [HtmlAttributeName("label")] public string Label { get; set; } = Localization.Text.T("Common.Sections");

    /// <summary>The key of the panel that is open (the first one when it is none of them).</summary>
    [HtmlAttributeName("active")] public string? Active { get; set; }

    public override void Init(TagHelperContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemsKey] = new PanelGroup(Id, Active);
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        var group = (PanelGroup)context.Items[ItemsKey];
        var content = (await output.GetChildContentAsync()).GetContent();
        var active = group.Panels.Any(p => p.Key == group.Active) ? group.Active : group.Panels.Count > 0 ? group.Panels[0].Key : null;
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "md-panels");
        output.Attributes.SetAttribute("data-tabs", null);
        output.Attributes.SetAttribute("data-panels", Id);
        var bar = new StringBuilder($"<div class=\"mat-tabs\" role=\"tablist\" aria-label=\"{E(Label)}\">");
        foreach (var p in group.Panels)
        {
            var on = p.Key == active;
            var icon = p.Icon is null ? string.Empty : Ui.Icon(p.Icon).ToString();
            var mark = p.Marked ? "<span class=\"md-tab-mark\" aria-hidden=\"true\"></span>" : string.Empty;
            bar.Append($"<button type=\"button\" class=\"mat-tab\" role=\"tab\" id=\"{E(Id)}-tab-{E(p.Key)}\" aria-controls=\"{E(Id)}-panel-{E(p.Key)}\" aria-selected=\"{(on ? "true" : "false")}\"{(on ? string.Empty : " tabindex=\"-1\"")} data-panel-key=\"{E(p.Key)}\">{icon}<span>{E(p.Title)}</span>{mark}</button>");
        }

        bar.Append("</div>");

        output.Content.SetHtmlContent(bar + content);
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    internal sealed class PanelGroup(string id, string? active)
    {
        public string Id { get; } = id;

        public string? Active { get; } = active;

        public List<(string Key, string Title, string? Icon, bool Marked)> Panels { get; } = [];
    }
}

/// <summary>One panel of <see cref="PanelTabsTagHelper"/>: its key (unique in the group), the title of its tab, an icon, and a dot on the tab (<c>marked</c>: something to see there).</summary>
[HtmlTargetElement("md-panel", ParentTag = "md-panels")]
public sealed class PanelTagHelper : TagHelper
{
    [HtmlAttributeName("key")] public string Key { get; set; } = string.Empty;

    [HtmlAttributeName("title")] public string Title { get; set; } = string.Empty;

    [HtmlAttributeName("icon")] public string? Icon { get; set; }

    [HtmlAttributeName("marked")] public bool Marked { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        var group = (PanelTabsTagHelper.PanelGroup)context.Items[PanelTabsTagHelper.ItemsKey];
        var first = group.Panels.Count == 0;
        group.Panels.Add((Key, Title, Icon, Marked));
        var open = group.Active == Key || (first && group.Active is null);
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-tabpanel");
        output.Attributes.SetAttribute("role", "tabpanel");
        output.Attributes.SetAttribute("id", $"{group.Id}-panel-{Key}");
        output.Attributes.SetAttribute("aria-labelledby", $"{group.Id}-tab-{Key}");
        output.Attributes.SetAttribute("data-panel-key", Key);
        if (!open)
        {
            output.Attributes.Add(new TagHelperAttribute("hidden"));
        }
    }
}
