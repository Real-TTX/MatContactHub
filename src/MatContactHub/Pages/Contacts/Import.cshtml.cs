using System.Globalization;
using MatContactHub.Controls;
using MatContactHub.Data;
using MatContactHub.Localization;
using MatContactHub.Services;
using MatContactHub.Services.Contacts;
using Microsoft.AspNetCore.Mvc;

namespace MatContactHub.Pages.Contacts;

/// <summary>
/// The import of a vCard or CSV file into an address book where the person is an editor (module C): the file and the address book, then a preview (the columns
/// of a CSV file and the first records, how many are wrong or already there), then the import. The file waits between the steps in a temporary folder
/// (<see cref="ContactImport.Stash"/>) under a key of this person.
/// </summary>
[RequestSizeLimit(ContactImport.MaxBytes + 64 * 1024)]
public sealed class ImportModel(AppDbContext db) : AppPageModel
{
    [BindProperty] public long AddressBookId { get; set; }

    [BindProperty] public bool SkipDuplicates { get; set; } = true;

    [BindProperty] public IFormFile? Upload { get; set; }

    /// <summary>The key of the uploaded file between the preview and the import.</summary>
    [BindProperty] public string? Key { get; set; }

    public ImportPreview? Preview { get; private set; }

    public string? FileName { get; private set; }

    public List<Choice> AddressBooks { get; private set; } = [];

    public string AddressBookName => AddressBooks.FirstOrDefault(b => b.Value == AddressBookId.ToString(CultureInfo.InvariantCulture))?.Label ?? string.Empty;

    public async Task<IActionResult> OnGetAsync(long? book)
    {
        if (await ContactService.DefaultAddressBookAsync(db, Actor.ForUser(Me), book, HttpContext.RequestAborted) is not { } target)
        {
            return NotFound();
        }

        AddressBookId = target;
        await LoadBooksAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var actor = Actor.ForUser(Me);
        await LoadBooksAsync();
        if (AddressBooks.Count == 0)
        {
            return NotFound();
        }

        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(Upload), Text.T("Contacts.Import.NoFile"));
            return Page();
        }

        if (Upload.Length > ContactImport.MaxBytes)
        {
            ModelState.AddModelError(nameof(Upload), Text.T("Contacts.Import.TooLarge", ContactImport.MaxBytes / 1024 / 1024));
            return Page();
        }

        using var buffer = new MemoryStream();
        await Upload.CopyToAsync(buffer, HttpContext.RequestAborted);
        var text = ContactImport.Decode(buffer.ToArray());
        Preview = await ContactImport.PreviewAsync(db, actor, AddressBookId, text, SkipDuplicates, HttpContext.RequestAborted);
        if (Preview is null)
        {
            ModelState.AddModelError(nameof(AddressBookId), Text.T("Contacts.AddressBook.NotAllowed"));
            return Page();
        }

        if (Preview.Records == 0)
        {
            Preview = null;
            ModelState.AddModelError(nameof(Upload), Text.T("Contacts.Import.Empty"));
            return Page();
        }

        FileName = Path.GetFileName(Upload.FileName);
        Key = ContactImport.Stash(actor, text);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync()
    {
        var actor = Actor.ForUser(Me);
        if (ContactImport.Peek(actor, Key) is not { } text)
        {
            FlashError(Text.T("Contacts.Import.Expired"));
            return LocalRedirect("/Contacts/Import");
        }

        var result = await ContactImport.ImportAsync(db, actor, AddressBookId, text, SkipDuplicates, HttpContext.RequestAborted);
        if (result.NotAllowed)
        {
            return NotFound();
        }

        ContactImport.Drop(actor, Key);
        if (!result.Ok)
        {
            FlashError(result.Error!);
            return LocalRedirect("/Contacts/Import");
        }

        Flash(Text.T("Contacts.Import.Done", result.Value!.Created, result.Value.Duplicates, result.Value.Invalid));
        return LocalRedirect($"/Contacts?book={AddressBookId}&sort=changed&dir=desc");
    }

    private async Task LoadBooksAsync()
    {
        var books = await AddressBookService.ListAsync(db, Actor.ForUser(Me), HttpContext.RequestAborted);
        AddressBooks = [.. books.Where(b => b.Role >= AddressBookRole.Editor).Select(b => new Choice(b.Id.ToString(CultureInfo.InvariantCulture), b.Name, null, b.Color))];
    }
}
