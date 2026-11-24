using MatContactHub.Data;
using MatContactHub.Services;
using MatContactHub.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Shared;

/// <summary>
/// The menu of the layout, built through the business layer (<see cref="MenuService"/>) once per request: the panels of the menu (<c>part="nav"</c>,
/// Components/Menu/Default.cshtml) and the button New contact of the top bar (<c>part="create"</c>, Components/Menu/Create.cshtml).
/// </summary>
public sealed class MenuViewComponent(AppDbContext db, CurrentUser me) : ViewComponent
{
    private const string ItemKey = "MatContactHub.Menu";

    public async Task<IViewComponentResult> InvokeAsync(string part, string area)
    {
        if (HttpContext.Items[ItemKey] is not MenuModel menu)
        {
            menu = await MenuService.GetAsync(db, Actor.ForUser(me), HttpContext.RequestAborted);
            HttpContext.Items[ItemKey] = menu;
        }

        return part == "create" ? View("Create", menu) : View((menu, area));
    }
}
