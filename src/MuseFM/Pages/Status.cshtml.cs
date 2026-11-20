using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MuseFM.Pages;

[IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int Code { get; private set; }
    public string Heading { get; private set; } = "";
    public string Message { get; private set; } = "";

    public void OnGet(int code) => Set(code);

    public void OnPost(int code) => Set(code);

    private void Set(int code)
    {
        Code = code;
        (Heading, Message) = code switch
        {
            404 => ("Not found", "This page does not exist, or the profile is private."),
            403 => ("Forbidden", "You do not have access to this page."),
            >= 500 => ("Something went wrong", "An unexpected error occurred. Check the server logs for details."),
            _ => ("Error", "The request could not be completed."),
        };
    }
}
