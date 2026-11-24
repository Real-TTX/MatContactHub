namespace MatContactHub.Pages.Admin;

/// <summary>
/// The connections of MatContactHub to other apps (the Mat app model): the Connections area of every app (<c>&lt;mat-connections /&gt;</c> of MatPlatform: what MatContactHub uses
/// – the sign-in through MatIdentity and the mail through MatSMTP –, how each was set, what it adds to a home screen). Mattiverse makes the bindings by itself.
/// </summary>
public sealed class ConnectionsModel : AppPageModel
{
    public void OnGet()
    {
    }
}
