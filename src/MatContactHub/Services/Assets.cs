using System.Text;

namespace MatContactHub.Services;

/// <summary>The icon sprite, which must be in the page for <c>&lt;use href="#..."&gt;</c>: the toolkit's and the app's own, on every page.</summary>
public sealed class Assets(IWebHostEnvironment env)
{
    public string Sprite { get; } = Read(env, "matui/matui-icons.svg") + Read(env, "img/app-icons.svg");

    private static string Read(IWebHostEnvironment env, string path)
    {
        var file = env.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
