namespace Stomme.Maui;

public static class MauiConstants
{
    // the API service url (used as fallback if not set in appsettings.json)
    public static string ApiUrl = ""; 
    //public static string ApiUrl = "http://localhost:44357/"; // for local development (Stomme.Server must be already running for MAUI client to connect)
    //public static string apiurl = "http://localhost:44357/sitename/"; // local microsite example

    // specify if you wish to allow users to override the url via appsettings.json in the AppDataDirectory
    public static bool UseAppSettings = true;
}
