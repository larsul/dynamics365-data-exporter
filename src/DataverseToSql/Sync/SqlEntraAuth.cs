using Microsoft.Data.SqlClient;

namespace DataverseToSql.Sync;

/// <summary>
/// Registers the Microsoft Entra interactive provider with a parent window handle. On Windows, MSAL signs in through the
/// WAM broker, which refuses to show its dialog without a parent window ("0xwindow_handle_required").
/// </summary>
public static class SqlEntraAuth
{
    private static IntPtr s_parentWindow;
    private static bool s_registered;

    /// <summary>Call once from the UI thread after the main window handle exists. Later calls only update the handle.</summary>
    public static void UseParentWindow(IntPtr handle)
    {
        s_parentWindow = handle; // read by MSAL on any thread; the handle itself is thread-agnostic
        if (s_registered)
            return;

        var provider = new ActiveDirectoryAuthenticationProvider();
        provider.SetParentActivityOrWindowFunc(() => s_parentWindow);
        SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive, provider);
        s_registered = true;
    }
}
